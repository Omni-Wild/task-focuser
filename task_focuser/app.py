from __future__ import annotations

import sys

from PySide6.QtCore import QPoint, QTimer, Qt
from PySide6.QtGui import QAction, QActionGroup, QColor, QGuiApplication, QIcon, QPainter, QPen, QPixmap
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import QApplication, QMenu, QSystemTrayIcon

from . import palette, state
from .note import StickyNote

OPACITY_STEPS = [100, 90, 80, 70, 60, 50, 40, 30, 20]
SINGLETON_NAME = "task-focuser-singleton"


def _point_on_any_screen(x: int, y: int) -> bool:
    point = QPoint(x, y)
    for screen in QGuiApplication.screens():
        if screen.availableGeometry().contains(point):
            return True
    return False


def _make_tray_pixmap(hex_color: str, size: int = 32) -> QPixmap:
    pm = QPixmap(size, size)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing, True)
    p.setBrush(QColor(hex_color))
    pen = QPen(QColor("#1B1B1F"))
    pen.setWidth(1)
    p.setPen(pen)
    margin = 3
    p.drawRoundedRect(margin, margin, size - 2 * margin, size - 2 * margin, 6, 6)
    p.end()
    return pm


class TaskFocuserApp:
    def __init__(self, app: QApplication) -> None:
        self.app = app
        self.state = state.load()

        if not _point_on_any_screen(self.state["x"], self.state["y"]):
            self.state["x"], self.state["y"] = 100, 100

        self.note = StickyNote(color_hex=self.state["color"])
        self.note.set_text(self.state["text"])
        self.note.move(self.state["x"], self.state["y"])
        self.note.setWindowOpacity(self.state["opacity"] / 100.0)

        self._save_timer = QTimer()
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(250)
        self._save_timer.timeout.connect(self._flush_save)

        self.note.text_changed.connect(self._on_text_changed)
        self.note.moved.connect(self._on_moved)

        self._build_tray()
        self.note.show()
        if self.state.get("locked"):
            self.note.set_locked(True)

    # ---- tray ----
    def _build_tray(self) -> None:
        self.tray = QSystemTrayIcon(self.app)
        self.tray.setIcon(QIcon(_make_tray_pixmap(self.state["color"])))
        self.tray.setToolTip("Task Focuser")

        menu = QMenu()

        self.lock_action = QAction(self._lock_label(), menu)
        self.lock_action.setCheckable(True)
        self.lock_action.setChecked(bool(self.state["locked"]))
        self.lock_action.triggered.connect(self._toggle_lock)
        menu.addAction(self.lock_action)

        menu.addSeparator()

        color_menu = menu.addMenu("Color")
        self._color_group = QActionGroup(menu)
        self._color_group.setExclusive(True)
        self._color_actions: dict[str, QAction] = {}
        for name, hx in palette.COLORS:
            act = QAction(name, color_menu)
            act.setCheckable(True)
            act.setChecked(hx == self.state["color"])
            act.triggered.connect(lambda _checked=False, h=hx: self._set_color(h))
            self._color_group.addAction(act)
            color_menu.addAction(act)
            self._color_actions[hx] = act

        opacity_menu = menu.addMenu("Opacity")
        self._opacity_group = QActionGroup(menu)
        self._opacity_group.setExclusive(True)
        self._opacity_actions: dict[int, QAction] = {}
        for pct in OPACITY_STEPS:
            act = QAction(f"{pct}%", opacity_menu)
            act.setCheckable(True)
            act.setChecked(pct == int(self.state["opacity"]))
            act.triggered.connect(lambda _checked=False, p=pct: self._set_opacity(p))
            self._opacity_group.addAction(act)
            opacity_menu.addAction(act)
            self._opacity_actions[pct] = act

        menu.addSeparator()

        show_action = QAction("Show note", menu)
        show_action.triggered.connect(self._show_note)
        menu.addAction(show_action)

        reset_pos_action = QAction("Reset position", menu)
        reset_pos_action.triggered.connect(self._reset_position)
        menu.addAction(reset_pos_action)

        menu.addSeparator()

        quit_action = QAction("Quit", menu)
        quit_action.triggered.connect(self.app.quit)
        menu.addAction(quit_action)

        self.tray.setContextMenu(menu)
        self.tray.activated.connect(self._on_tray_activated)
        self.tray.show()

    def _lock_label(self) -> str:
        return "Unlock" if self.state.get("locked") else "Lock"

    def _on_tray_activated(self, reason: QSystemTrayIcon.ActivationReason) -> None:
        if reason == QSystemTrayIcon.Trigger:
            self._toggle_lock()

    # ---- handlers ----
    def _set_color(self, hex_str: str) -> None:
        self.state["color"] = hex_str
        self.note.set_color(hex_str)
        self.tray.setIcon(QIcon(_make_tray_pixmap(hex_str)))
        if hex_str in self._color_actions:
            self._color_actions[hex_str].setChecked(True)
        self._save_timer.start()

    def _set_opacity(self, pct: int) -> None:
        self.state["opacity"] = pct
        self.note.setWindowOpacity(pct / 100.0)
        if pct in self._opacity_actions:
            self._opacity_actions[pct].setChecked(True)
        self._save_timer.start()

    def _toggle_lock(self) -> None:
        new_locked = not bool(self.state.get("locked"))
        self.state["locked"] = new_locked
        self.note.set_locked(new_locked)
        self.lock_action.setText(self._lock_label())
        self.lock_action.setChecked(new_locked)
        self._save_timer.start()

    def _show_note(self) -> None:
        # Bring the note to front at its current position; do not move it.
        self.note.show()
        self.note.raise_()
        self.note.activateWindow()

    def _reset_position(self) -> None:
        self.note.move(100, 100)
        self.state["x"] = 100
        self.state["y"] = 100
        self.note.show()
        self.note.raise_()
        self.note.activateWindow()
        self._save_timer.start()

    def handle_remote_show(self) -> None:
        self._show_note()

    def _on_text_changed(self, text: str) -> None:
        self.state["text"] = text
        self._save_timer.start()

    def _on_moved(self, x: int, y: int) -> None:
        self.state["x"] = x
        self.state["y"] = y
        self._save_timer.start()

    def _flush_save(self) -> None:
        state.save(self.state)

    def save_now(self) -> None:
        self._save_timer.stop()
        state.save(self.state)


def _try_existing_instance() -> bool:
    """Return True if another Task Focuser instance is already running (and we should exit)."""
    sock = QLocalSocket()
    sock.connectToServer(SINGLETON_NAME)
    if sock.waitForConnected(150):
        sock.write(b"show")
        sock.flush()
        sock.waitForBytesWritten(150)
        sock.disconnectFromServer()
        return True
    return False


def _start_singleton_server(on_show) -> QLocalServer:
    # Remove any stale socket left from a crashed previous run.
    QLocalServer.removeServer(SINGLETON_NAME)
    server = QLocalServer()
    server.listen(SINGLETON_NAME)

    def _on_new_connection() -> None:
        client = server.nextPendingConnection()
        if client is None:
            return
        if client.waitForReadyRead(150):
            _ = client.readAll()
        on_show()
        client.disconnectFromServer()

    server.newConnection.connect(_on_new_connection)
    return server


def main() -> None:
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)

    if _try_existing_instance():
        return

    tf = TaskFocuserApp(app)
    tf._singleton_server = _start_singleton_server(tf.handle_remote_show)
    app.aboutToQuit.connect(tf.save_now)
    sys.exit(app.exec())
