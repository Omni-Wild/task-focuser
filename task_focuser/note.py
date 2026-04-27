from __future__ import annotations

import ctypes
import sys
from ctypes import wintypes

from PySide6.QtCore import QEvent, QPoint, QRect, QSize, Qt, Signal
from PySide6.QtGui import (
    QColor,
    QFont,
    QFontMetrics,
    QPainter,
    QPainterPath,
    QPen,
    QTextCursor,
    QTextOption,
)
from PySide6.QtWidgets import (
    QGraphicsDropShadowEffect,
    QTextEdit,
    QWidget,
)

from . import palette

CORNER_RADIUS = 10
PAD_X = 12
PAD_Y = 10
BORDER_W = 1.5
MIN_WIDTH = 160
MAX_WIDTH = 420
MAX_LINES = 3
FONT_PX = 14

# Win32 helpers: toggle click-through and force topmost-over-taskbar
# without recreating the HWND.
_GWL_EXSTYLE = -20
_WS_EX_TRANSPARENT = 0x00000020
_HWND_TOPMOST = wintypes.HWND(-1)
_SWP_NOMOVE = 0x0002
_SWP_NOSIZE = 0x0001
_SWP_NOACTIVATE = 0x0010
if sys.platform == "win32":
    _user32 = ctypes.windll.user32
    _user32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
    _user32.GetWindowLongW.restype = ctypes.c_long
    _user32.SetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_long]
    _user32.SetWindowLongW.restype = ctypes.c_long
    _user32.SetWindowPos.argtypes = [
        wintypes.HWND, wintypes.HWND,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        wintypes.UINT,
    ]
    _user32.SetWindowPos.restype = wintypes.BOOL
else:
    _user32 = None


def _set_click_through(hwnd: int, enabled: bool) -> None:
    if _user32 is None or not hwnd:
        return
    style = _user32.GetWindowLongW(hwnd, _GWL_EXSTYLE)
    if enabled:
        style |= _WS_EX_TRANSPARENT
    else:
        style &= ~_WS_EX_TRANSPARENT
    _user32.SetWindowLongW(hwnd, _GWL_EXSTYLE, style)


def _force_topmost(hwnd: int) -> None:
    """Re-assert HWND_TOPMOST so the window draws over the taskbar."""
    if _user32 is None or not hwnd:
        return
    _user32.SetWindowPos(
        wintypes.HWND(hwnd), _HWND_TOPMOST,
        0, 0, 0, 0,
        _SWP_NOMOVE | _SWP_NOSIZE | _SWP_NOACTIVATE,
    )


def _build_font() -> QFont:
    f = QFont("Inter")
    if f.family().lower() != "inter":
        f = QFont("Segoe UI")
    f.setPixelSize(FONT_PX)
    f.setWeight(QFont.Medium)
    return f


class StickyNote(QWidget):
    text_changed = Signal(str)
    moved = Signal(int, int)

    def __init__(self, color_hex: str = "#FFF9B1", parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground, True)

        self._color = color_hex
        self._locked = False

        self._font = _build_font()

        self._edit = QTextEdit(self)
        self._edit.setFrameStyle(0)
        self._edit.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self._edit.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self._edit.setWordWrapMode(QTextOption.WordWrap)
        self._edit.setFont(self._font)
        self._edit.setPlaceholderText("focus...")
        self._edit.setStyleSheet(
            f"QTextEdit {{ background: transparent; color: {palette.text_color()};"
            f" border: none; padding: 0px; }}"
        )
        self._edit.viewport().setAutoFillBackground(False)
        self._edit.setAttribute(Qt.WA_TranslucentBackground, True)

        self._last_text = ""
        self._edit.textChanged.connect(self._on_text_changed)
        self._edit.installEventFilter(self)
        self._edit.viewport().installEventFilter(self)

        shadow = QGraphicsDropShadowEffect(self)
        shadow.setBlurRadius(18)
        shadow.setOffset(0, 2)
        shadow.setColor(QColor(0, 0, 0, 60))
        self.setGraphicsEffect(shadow)

        self.resize(MIN_WIDTH, self._height_for_lines(1))
        self._layout_edit()

    # ---- public API ----
    def color(self) -> str:
        return self._color

    def set_color(self, hex_str: str) -> None:
        self._color = hex_str
        self.update()

    def is_locked(self) -> bool:
        return self._locked

    def set_locked(self, locked: bool) -> None:
        if locked == self._locked:
            return
        self._locked = locked
        self._edit.setReadOnly(locked)
        if locked:
            self._edit.clearFocus()
        # True OS-level click-through: flip WS_EX_TRANSPARENT on the existing HWND.
        # Avoids setWindowFlags, which would recreate the native window and break
        # the translucent fill / drop shadow on Windows.
        _set_click_through(int(self.winId()), locked)
        self.update()

    def set_text(self, text: str) -> None:
        if self._edit.toPlainText() == text:
            return
        self._edit.blockSignals(True)
        self._edit.setPlainText(text)
        self._edit.blockSignals(False)
        self._last_text = self._edit.toPlainText()
        self._auto_resize()

    def text(self) -> str:
        return self._edit.toPlainText()

    # ---- internal ----
    def _line_height(self) -> int:
        return QFontMetrics(self._font).lineSpacing()

    def _height_for_lines(self, n: int) -> int:
        n = max(1, min(MAX_LINES, n))
        return n * self._line_height() + 2 * PAD_Y

    def _layout_edit(self) -> None:
        self._edit.setGeometry(
            PAD_X, PAD_Y, max(0, self.width() - 2 * PAD_X), max(0, self.height() - 2 * PAD_Y)
        )

    def _auto_resize(self) -> None:
        text = self._edit.toPlainText() or self._edit.placeholderText()
        fm = QFontMetrics(self._font)
        text_max_w = MAX_WIDTH - 2 * PAD_X
        rect = fm.boundingRect(
            QRect(0, 0, text_max_w, 10_000),
            Qt.TextWordWrap,
            text,
        )
        new_w = max(MIN_WIDTH, min(MAX_WIDTH, rect.width() + 2 * PAD_X + 4))
        line_h = self._line_height()
        wrapped_lines = max(1, (rect.height() + line_h - 1) // line_h)
        wrapped_lines = min(MAX_LINES, wrapped_lines)
        new_h = self._height_for_lines(wrapped_lines)
        if (new_w, new_h) != (self.width(), self.height()):
            self.resize(new_w, new_h)

    def _on_text_changed(self) -> None:
        text = self._edit.toPlainText()
        if self._exceeds_line_cap(text):
            self._edit.blockSignals(True)
            self._edit.setPlainText(self._last_text)
            self._edit.blockSignals(False)
            cur = self._edit.textCursor()
            cur.movePosition(QTextCursor.End)
            self._edit.setTextCursor(cur)
            self._auto_resize()
            return
        self._last_text = text
        self._auto_resize()
        self.text_changed.emit(text)

    def _exceeds_line_cap(self, text: str) -> bool:
        if text.count("\n") + 1 > MAX_LINES:
            return True
        fm = QFontMetrics(self._font)
        text_max_w = MAX_WIDTH - 2 * PAD_X
        rect = fm.boundingRect(
            QRect(0, 0, text_max_w, 10_000),
            Qt.TextWordWrap,
            text,
        )
        line_h = fm.lineSpacing()
        wrapped_lines = max(1, (rect.height() + line_h - 1) // line_h)
        return wrapped_lines > MAX_LINES

    # ---- drag (uses Qt's native system move) ----
    def _start_native_drag(self) -> bool:
        wh = self.windowHandle()
        if wh is None:
            return False
        started = wh.startSystemMove()
        if started:
            # Position is updated by the OS during drag; emit when it ends.
            # We piggy-back on moveEvent below to save the final position.
            self._dragging_via_system = True
        return bool(started)

    def moveEvent(self, event) -> None:  # noqa: N802
        super().moveEvent(event)
        if getattr(self, "_dragging_via_system", False):
            self.moved.emit(self.x(), self.y())

    # ---- Qt events ----
    def showEvent(self, event) -> None:  # noqa: N802
        super().showEvent(event)
        # Re-assert topmost so we render above the taskbar after every show().
        _force_topmost(int(self.winId()))

    def resizeEvent(self, event) -> None:  # noqa: N802
        super().resizeEvent(event)
        self._layout_edit()

    def paintEvent(self, event) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing, True)
        rect = self.rect().adjusted(0, 0, -1, -1)
        path = QPainterPath()
        path.addRoundedRect(rect, CORNER_RADIUS, CORNER_RADIUS)
        p.fillPath(path, QColor(self._color))
        if not self._locked:
            pen = QPen(QColor(palette.darken(self._color)))
            pen.setWidthF(BORDER_W)
            p.setPen(pen)
            p.drawPath(path)

    def _press_in_edit_text_area(self, pos: QPoint) -> bool:
        return self._edit.geometry().contains(pos)

    def eventFilter(self, watched, event) -> bool:  # noqa: N802
        if self._locked:
            return False
        et = event.type()
        if et == QEvent.MouseButtonPress:
            if event.button() == Qt.LeftButton and (event.modifiers() & Qt.AltModifier):
                if self._start_native_drag():
                    return True
        return super().eventFilter(watched, event)

    def mousePressEvent(self, event) -> None:  # noqa: N802
        if self._locked:
            return
        if event.button() != Qt.LeftButton:
            return
        in_text = self._press_in_edit_text_area(event.position().toPoint())
        alt_held = bool(event.modifiers() & Qt.AltModifier)
        if (not in_text) or alt_held:
            if self._start_native_drag():
                event.accept()
                return
        super().mousePressEvent(event)

    def sizeHint(self) -> QSize:  # noqa: N802
        return QSize(MIN_WIDTH, self._height_for_lines(1))
