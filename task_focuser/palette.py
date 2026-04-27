COLORS: list[tuple[str, str]] = [
    ("Yellow", "#FFF9B1"),
    ("Orange", "#FFD8A8"),
    ("Pink",   "#FFCEE0"),
    ("Red",    "#FCC0B6"),
    ("Green",  "#D5F692"),
    ("Teal",   "#BFF0E5"),
    ("Blue",   "#A6CCF5"),
    ("Purple", "#D5C6E0"),
    ("Gray",   "#E6E6E6"),
    ("White",  "#FFFFFF"),
]

TEXT_COLOR = "#1B1B1F"


def darken(hex_str: str, factor: float = 0.82) -> str:
    s = hex_str.lstrip("#")
    if len(s) != 6:
        raise ValueError(f"expected #RRGGBB, got {hex_str!r}")
    r, g, b = int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16)
    r = max(0, min(255, int(r * factor)))
    g = max(0, min(255, int(g * factor)))
    b = max(0, min(255, int(b * factor)))
    return f"#{r:02X}{g:02X}{b:02X}"


def text_color() -> str:
    return TEXT_COLOR
