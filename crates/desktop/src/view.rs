//! Native widgets matching the original compact paper, settings row, and bird picker.

use crate::{
    app::{App, EDITOR_ID, Message, PAD, RENAME_ID},
    canvas::{self, Glyph, INK, Icon, alpha, darken, hex},
    surface::SurfaceRole,
};
use iced::{
    Border, Color, Element, Font, Length, Padding, Size,
    advanced::text::{self as shaping, Paragraph},
    alignment,
    keyboard::{Key, key},
    widget::{
        Canvas, Space, button, column, container, mouse_area, row, slider, stack, text,
        text_editor, text_input, tooltip,
    },
};
use task_focuser_core::{
    audio,
    sprites::BirdId,
    state::{COLORS, IdleRate, IdleSounds},
};

pub fn view(app: &App, role: SurfaceRole) -> Element<'_, Message> {
    match role {
        SurfaceRole::Note => note(app),
        SurfaceRole::Dot => mouse_area(canvas::artwork(app, role, 96., 96.))
            .on_press(Message::Press(role))
            .on_double_click(Message::DoubleClick(role))
            .on_release(Message::Release)
            .interaction(iced::mouse::Interaction::Pointer)
            .into(),
        SurfaceRole::Bird => mouse_area(canvas::artwork(
            app,
            role,
            app.surfaces.get(&role).map_or(90., |r| r.width as f32),
            app.surfaces.get(&role).map_or(72., |r| r.height as f32),
        ))
        .on_press(Message::BirdPress)
        .on_enter(Message::BirdHover(true))
        .on_exit(Message::BirdHover(false))
        .on_release(Message::Release)
        .interaction(iced::mouse::Interaction::Pointer)
        .into(),
        SurfaceRole::CloseTarget => canvas::artwork(app, role, 80., 80.),
        SurfaceRole::Cucumber => canvas::artwork(app, role, 28., 52.),
    }
}

fn note(app: &App) -> Element<'_, Message> {
    let state = app.state();
    let paper = app.paper_bounds();
    let width = paper.width as f32;
    let height = paper.height as f32;
    let stage = app.note_stage();
    let opacity = f32::from(state.opacity) / 100. * app.note_alpha();
    let ink = alpha(INK, opacity);
    let mut layers = vec![canvas::artwork(
        app,
        SurfaceRole::Note,
        stage.width as f32,
        stage.height as f32,
    )];
    let body: Element<'_, Message> = if state.locked {
        let label = text(&state.text)
            .font(note_font(&state.font))
            .size(f32::from(state.font_size))
            .line_height(shaping::LineHeight::Relative(1.2))
            .shaping(shaping::Shaping::Advanced)
            .wrapping(shaping::Wrapping::WordOrGlyph)
            .color(ink)
            .width(width - 28.);
        let folded_bar = container(canvas::NoteTransform::visual(settings(app)))
            .height(34. * app.unfold() as f32)
            .clip(true);
        mouse_area(
            column![
                Space::new().height(6.),
                folded_bar,
                container(label).padding([10., 14.])
            ]
            .width(width)
            .height(height),
        )
        .on_press(Message::Press(SurfaceRole::Note))
        .on_double_click(Message::DoubleClick(SurfaceRole::Note))
        .on_release(Message::Release)
        .interaction(iced::mouse::Interaction::Grab)
        .into()
    } else {
        let strip = mouse_area(Space::new().width(width).height(6.))
            .on_press(Message::Press(SurfaceRole::Note))
            .on_double_click(Message::DoubleClick(SurfaceRole::Note))
            .on_release(Message::Release)
            .interaction(iced::mouse::Interaction::Grab);
        let editor = text_editor(&app.editor)
            .id(EDITOR_ID)
            .on_action(Message::Edit)
            .font(note_font(&state.font))
            .size(f32::from(state.font_size))
            .line_height(shaping::LineHeight::Relative(1.2))
            .wrapping(shaping::Wrapping::WordOrGlyph)
            .padding(0)
            .height(
                (height - 26. - 34. * app.unfold() as f32).max(f32::from(state.font_size) * 1.2),
            )
            .key_binding(|event| match event.key.as_ref() {
                Key::Named(key::Named::Escape) => Some(text_editor::Binding::Custom(
                    Message::Command(task_focuser_core::conductor::Command::Lock),
                )),
                Key::Character("l") if event.modifiers.command() => {
                    Some(text_editor::Binding::Custom(Message::Command(
                        task_focuser_core::conductor::Command::Lock,
                    )))
                }
                Key::Character("+") | Key::Character("=") if event.modifiers.command() => {
                    Some(text_editor::Binding::Custom(Message::FontSize(1)))
                }
                Key::Character("-") if event.modifiers.command() => {
                    Some(text_editor::Binding::Custom(Message::FontSize(-1)))
                }
                Key::Character("z") if event.modifiers.command() => Some(
                    text_editor::Binding::Custom(Message::Undo(event.modifiers.shift())),
                ),
                Key::Character("y") if event.modifiers.command() => {
                    Some(text_editor::Binding::Custom(Message::Undo(true)))
                }
                _ => text_editor::Binding::from_key_press(event),
            })
            .style(move |_, _| text_editor::Style {
                background: Color::TRANSPARENT.into(),
                border: Border::default(),
                placeholder: ink,
                value: ink,
                selection: Color::from_rgba8(0x4F, 0x8D, 0xFF, 0.32 * opacity),
            });
        column![
            strip,
            container(settings(app))
                .height(34. * app.unfold() as f32)
                .clip(true),
            container(editor).padding([10., 14.]).width(width)
        ]
        .width(width)
        .height(height)
        .into()
    };
    layers.push(position(body, PAD as f32, PAD as f32));
    if !state.locked {
        let grip = mouse_area(
            Canvas::new(Glyph {
                icon: Icon::Grip,
                opacity,
            })
            .width(18.)
            .height(18.),
        )
        .on_press(Message::ResizePress)
        .on_release(Message::Release)
        .interaction(iced::mouse::Interaction::ResizingDiagonallyDown);
        layers.push(position(
            grip.into(),
            PAD as f32 + width - 19.,
            PAD as f32 + height - 19.,
        ));
        if app.volume_open {
            layers.push(position(
                volume_popup(app),
                PAD as f32 + 126.,
                PAD as f32 + 44.,
            ));
        }
        if app.picker_open {
            layers.push(position(picker(app), PAD as f32 + 2., PAD as f32 + 42.));
        }
    }
    if let Some(error) = &app.error {
        layers.push(position(
            container(
                text(error)
                    .size(11)
                    .color(Color::from_rgb8(0xA8, 0x1E, 0x1E)),
            )
            .width((width - 16.).max(1.))
            .style(move |_| container::Style {
                background: Some(hex(&state.color).into()),
                ..Default::default()
            })
            .into(),
            PAD as f32 + 8.,
            PAD as f32 + height - 16.,
        ));
    }
    canvas::NoteTransform::wrap(
        stack(layers)
            .width(stage.width as f32)
            .height(stage.height as f32)
            .into(),
        app.note_scale(),
        iced::Point::new(PAD as f32 + width, PAD as f32),
        app.note_shake(),
    )
}

fn position<'a>(content: Element<'a, Message>, x: f32, y: f32) -> Element<'a, Message> {
    column![Space::new().height(y), row![Space::new().width(x), content]].into()
}

fn settings(app: &App) -> Element<'_, Message> {
    let state = app.state();
    let opacity = f32::from(state.opacity) / 100. * app.note_alpha();
    let full_name = state.bird_name(state.active_bird_id());
    let name = fit_name(full_name, Font::DEFAULT, 10.5, 52.);
    let name_button = tooltip(
        button(
            text(name)
                .font(Font::DEFAULT)
                .size(10.5)
                .shaping(shaping::Shaping::Advanced)
                .wrapping(shaping::Wrapping::None)
                .width(52.)
                .color(alpha(INK, 0.75 * opacity)),
        )
        .padding([0., 2.])
        .width(56.)
        .height(22.)
        .on_press(Message::TogglePicker)
        .style(move |_, status| flat_button(status, opacity)),
        text(full_name).size(11),
        tooltip::Position::Bottom,
    );
    let cucumber = mouse_area(
        Canvas::new(Glyph {
            icon: Icon::Cucumber,
            opacity,
        })
        .width(20.)
        .height(22.),
    )
    .on_press(Message::CucumberPress)
    .on_release(Message::Release)
    .interaction(iced::mouse::Interaction::Grab);
    let sound = mouse_area(icon_button(
        Icon::Sound(state.sound_on),
        Message::ToggleSound,
        "Sound on or off. Hover for volume and idle sounds",
        opacity,
    ))
    .on_enter(Message::VolumeHover(true))
    .on_exit(Message::VolumeHover(false));
    let swatches = row(COLORS.iter().map(|swatch| {
        let color = hex(swatch.hex);
        let selected = state.color.eq_ignore_ascii_case(swatch.hex);
        let swatch_button = button(Space::new().width(11.).height(11.))
            .padding(1)
            .width(15.)
            .height(17.)
            .on_press(Message::Color(swatch.hex))
            .style(move |_, status| button::Style {
                background: Some(alpha(color, opacity).into()),
                border: Border {
                    color: alpha(darken(color, 0.30), opacity),
                    width: if selected { 2. } else { 1. },
                    radius: 8.0.into(),
                },
                text_color: INK,
                ..flat_button(status, opacity)
            });
        tooltip(
            swatch_button,
            text(swatch.name).size(11),
            tooltip::Position::Bottom,
        )
        .into()
    }))
    .spacing(2)
    .align_y(alignment::Vertical::Center);
    let opacity_slider = tooltip(
        slider(20..=100, state.opacity, Message::Opacity)
            .width(52.)
            .height(18.)
            .style(move |_, status| slider_style(status, opacity)),
        text("Opacity").size(11),
        tooltip::Position::Bottom,
    );
    let font = mouse_area(
        button(
            text("f")
                .font(note_font(&state.font))
                .size(15)
                .color(alpha(INK, opacity)),
        )
        .padding(0)
        .width(20.)
        .height(24.)
        .on_press(Message::Font(1))
        .style(move |_, status| flat_button(status, opacity)),
    )
    .on_right_press(Message::Font(-1));
    let controls = row![
        name_button,
        cucumber,
        icon_button(
            Icon::Pinned(state.bird_stays),
            Message::ToggleStays,
            "Bird stays when locked",
            opacity
        ),
        icon_button(
            Icon::Opaque(state.bird_opaque),
            Message::ToggleOpaque,
            "Bird always opaque",
            opacity
        ),
        sound,
        swatches,
        Space::new().width(4.),
        opacity_slider,
        Space::new().width(4.),
        small_text_button("−", Message::FontSize(-1), opacity),
        tooltip(
            font,
            text(format!("Font: {}", state.font)).size(11),
            tooltip::Position::Bottom
        ),
        small_text_button("+", Message::FontSize(1), opacity),
        icon_button(
            Icon::Plants(state.plants_on),
            Message::TogglePlants,
            "Plants in the corner",
            opacity
        ),
        Space::new().width(Length::Fill),
        icon_button(
            Icon::Lock,
            Message::Command(task_focuser_core::conductor::Command::Lock),
            "Lock (Esc)",
            opacity
        )
    ]
    .spacing(0)
    .align_y(alignment::Vertical::Center)
    .height(34.);
    mouse_area(
        container(controls)
            .padding(Padding {
                left: 0.,
                right: 4.,
                top: 0.,
                bottom: 0.,
            })
            .width(app.conductor.note_bounds().width as f32)
            .height(34.),
    )
    .on_press(Message::Press(SurfaceRole::Note))
    .on_double_click(Message::DoubleClick(SurfaceRole::Note))
    .on_release(Message::Release)
    .into()
}

fn flat_button(status: button::Status, opacity: f32) -> button::Style {
    button::Style {
        background: match status {
            button::Status::Hovered => Some(alpha(INK, 0.10 * opacity).into()),
            button::Status::Pressed => Some(alpha(INK, 0.18 * opacity).into()),
            _ => None,
        },
        text_color: alpha(INK, opacity),
        border: Border {
            radius: 4.0.into(),
            ..Default::default()
        },
        ..Default::default()
    }
}
fn small_text_button(
    label: &'static str,
    message: Message,
    opacity: f32,
) -> Element<'static, Message> {
    button(text(label).size(15).color(alpha(INK, opacity)))
        .padding(0)
        .width(20.)
        .height(24.)
        .on_press(message)
        .style(move |_, status| flat_button(status, opacity))
        .into()
}
fn icon_button(
    icon: Icon,
    message: Message,
    label: &'static str,
    opacity: f32,
) -> Element<'static, Message> {
    tooltip(
        button(Canvas::new(Glyph { icon, opacity }).width(18.).height(20.))
            .padding([0., 2.])
            .width(22.)
            .height(24.)
            .on_press(message)
            .style(move |_, status| flat_button(status, opacity)),
        text(label).size(11),
        tooltip::Position::Top,
    )
    .into()
}
fn slider_style(status: slider::Status, opacity: f32) -> slider::Style {
    let active = matches!(status, slider::Status::Hovered | slider::Status::Dragged);
    slider::Style {
        rail: slider::Rail {
            backgrounds: (
                alpha(INK, 0.4 * opacity).into(),
                alpha(INK, 0.2 * opacity).into(),
            ),
            width: 2.,
            border: Border {
                radius: 2.0.into(),
                ..Default::default()
            },
        },
        handle: slider::Handle {
            shape: slider::HandleShape::Circle {
                radius: if active { 4. } else { 3.5 },
            },
            background: alpha(INK, 0.75 * opacity).into(),
            border_width: 0.,
            border_color: Color::TRANSPARENT,
        },
    }
}

fn volume_popup(app: &App) -> Element<'_, Message> {
    let state = app.state();
    let opacity = f32::from(state.opacity) / 100.;
    let sounds = row([
        ("chirp", IdleSounds::Chirp),
        ("brrrp", IdleSounds::Ruffle),
        ("both", IdleSounds::Both),
    ]
    .map(|(label, value)| {
        choice(
            label,
            Message::IdleSounds(value),
            state.idle_sounds == value,
            opacity,
        )
    }))
    .spacing(2);
    let rates = row([
        ("often", IdleRate::Often),
        ("mid", IdleRate::Mid),
        ("rarely", IdleRate::Rarely),
    ]
    .map(|(label, value)| {
        choice(
            label,
            Message::IdleRate(value),
            state.idle_rate == value,
            opacity,
        )
    }))
    .spacing(2);
    let contents = column![
        slider(
            0.0..=1.0,
            audio::volume_to_slider(state.volume),
            Message::Volume
        )
        .width(144.)
        .height(18.)
        .style(move |_, status| slider_style(
            status,
            opacity * if state.sound_on { 1. } else { 0.45 }
        )),
        sounds,
        rates
    ]
    .spacing(4);
    mouse_area(
        container(contents)
            .padding(8)
            .width(164.)
            .style(move |_| popup_style(&state.color, opacity)),
    )
    .on_enter(Message::VolumePopupHover(true))
    .on_exit(Message::VolumePopupHover(false))
    .into()
}
fn choice(
    label: &'static str,
    message: Message,
    chosen: bool,
    opacity: f32,
) -> Element<'static, Message> {
    let ink = alpha(INK, opacity * if chosen { 1. } else { 0.6 });
    let mut content =
        column![text(label).size(11).color(ink)].align_x(alignment::Horizontal::Center);
    if chosen {
        content = content.push(
            container(Space::new().width(18.).height(1.)).style(move |_| container::Style {
                background: Some(ink.into()),
                ..Default::default()
            }),
        );
    }
    button(content)
        .padding([0., 3.])
        .width(46.)
        .height(17.)
        .on_press(message)
        .style(move |_, status| flat_button(status, opacity))
        .into()
}
fn popup_style(color: &str, opacity: f32) -> container::Style {
    container::Style {
        background: Some(alpha(hex(color), opacity).into()),
        border: Border {
            color: alpha(darken(hex(color), 0.12), opacity),
            width: 1.,
            radius: 6.0.into(),
        },
        ..Default::default()
    }
}

fn picker(app: &App) -> Element<'_, Message> {
    let state = app.state();
    let opacity = f32::from(state.opacity) / 100.;
    let rows = column(BirdId::ALL.into_iter().map(|bird| {
        let id = bird.id();
        let portrait = Canvas::new(canvas::Portrait(bird)).width(33.).height(24.);
        let content: Element<'_, Message> = if app.renaming == Some(id) {
            let valid = app.rename_text.trim().chars().count() >= 3;
            text_input("Name", &app.rename_text)
                .id(RENAME_ID)
                .on_input(Message::RenameText)
                .on_submit(Message::FinishRename(true))
                .size(12)
                .width(94.)
                .padding([1., 3.])
                .style(move |theme, status| {
                    let mut style = text_input::default(theme, status);
                    style.background = Color::from_rgba8(0xFF, 0xFD, 0xF6, 0.7).into();
                    style.value = INK;
                    style.border = Border {
                        color: if valid {
                            alpha(INK, 0.35)
                        } else {
                            Color::from_rgb8(0xD0, 0x4A, 0x3A)
                        },
                        width: 1.,
                        radius: 3.0.into(),
                    };
                    style
                })
                .into()
        } else {
            let full_name = state.bird_name(id);
            let name = fit_name(full_name, Font::DEFAULT, 12., 85.);
            tooltip(
                button(
                    row![
                        portrait,
                        text(name)
                            .font(Font::DEFAULT)
                            .size(12)
                            .shaping(shaping::Shaping::Advanced)
                            .wrapping(shaping::Wrapping::None)
                            .width(85.)
                            .color(alpha(INK, opacity))
                    ]
                    .spacing(6)
                    .align_y(alignment::Vertical::Center),
                )
                .padding([3., 4.])
                .width(132.)
                .height(32.)
                .on_press(Message::PickBird(id))
                .style(move |_, status| flat_button(status, opacity)),
                text(full_name).size(11),
                tooltip::Position::Bottom,
            )
            .into()
        };
        row![
            content,
            icon_button(Icon::Pencil, Message::Rename(id), "Rename", opacity)
        ]
        .align_y(alignment::Vertical::Center)
        .height(32.)
        .into()
    }))
    .spacing(2);
    container(rows)
        .padding(4)
        .width(166.)
        .style(move |_| popup_style(&state.color, opacity))
        .into()
}

/// Shortens only the visible label. The picker and saved state retain the complete name.
fn fit_name(name: &str, font: Font, size: f32, width: f32) -> String {
    let mut label = name.replace(['\n', '\r', '\t'], " ");
    if measure_text(&label, font, size, f32::INFINITY).width <= width {
        return label;
    }
    while label.pop().is_some() {
        label.push('…');
        if measure_text(&label, font, size, f32::INFINITY).width <= width {
            return label;
        }
        label.pop();
    }
    String::new()
}

/// Persist original font names; platform families provide a matching fallback off Windows.
pub fn note_font(name: &str) -> Font {
    #[cfg(target_os = "windows")]
    let family = match name {
        "Segoe Print" => "Segoe Print",
        "Comic Sans MS" => "Comic Sans MS",
        "Cascadia Code" => "Cascadia Code",
        "Bahnschrift" => "Bahnschrift",
        _ => "Segoe UI",
    };
    #[cfg(target_os = "macos")]
    let family = match name {
        "Segoe Print" | "Comic Sans MS" => "Comic Sans MS",
        "Cascadia Code" => "Menlo",
        "Bahnschrift" => "Helvetica Neue",
        _ => "Helvetica Neue",
    };
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    let family = match name {
        "Segoe Print" => "Comic Neue",
        "Comic Sans MS" => "Comic Sans MS",
        "Cascadia Code" => "DejaVu Sans Mono",
        "Bahnschrift" => "DejaVu Sans",
        _ => "DejaVu Sans",
    };
    Font {
        family: iced::font::Family::Name(family),
        ..Font::DEFAULT
    }
}

pub fn measure_text(content: &str, font: Font, size: f32, width: f32) -> Size {
    let paragraph = iced::advanced::graphics::text::Paragraph::with_text(shaping::Text {
        content,
        bounds: Size::new(width, f32::INFINITY),
        size: iced::Pixels(size),
        line_height: shaping::LineHeight::Relative(1.2),
        font,
        align_x: shaping::Alignment::Default,
        align_y: alignment::Vertical::Top,
        shaping: shaping::Shaping::Advanced,
        wrapping: if width.is_finite() {
            shaping::Wrapping::WordOrGlyph
        } else {
            shaping::Wrapping::None
        },
    });
    let bounds = paragraph.min_bounds();
    Size::new(bounds.width, bounds.height.max(size * 1.2))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bird_labels_fit_their_real_font_width_for_wide_and_unicode_names() {
        for (size, width) in [(10.5, 52.), (12., 85.)] {
            for name in [
                "WWWWWWWWWWWWWW",
                "界界界界界界界界界界界界界界",
                "👩‍💻👩‍💻👩‍💻👩‍💻",
                "ЖЖЖЖЖЖЖЖЖЖЖЖЖЖ",
            ] {
                let label = fit_name(name, Font::DEFAULT, size, width);
                assert!(
                    measure_text(&label, Font::DEFAULT, size, f32::INFINITY).width <= width,
                    "{name:?} overflowed as {label:?}"
                );
                if name.starts_with('W') {
                    assert!(label.ends_with('…'));
                }
            }
        }
        assert_eq!(fit_name("Lennie", Font::DEFAULT, 10.5, 52.), "Lennie");
    }

    #[test]
    fn native_font_metrics_keep_blank_lines_and_wrap_long_words() {
        let font = note_font("Segoe UI");
        let line = measure_text("line", font, 15., 200.);
        let two_lines = measure_text("line\n", font, 15., 200.);
        assert!(
            two_lines.height >= line.height * 1.9,
            "trailing newline needs its editable blank line: {line:?} -> {two_lines:?}"
        );
        let long = "https://example.test/a-very-long-unbroken-note-value-with-more-characters";
        let wide = measure_text(long, font, 15., f32::INFINITY);
        let narrow = measure_text(long, font, 15., 100.);
        assert!(wide.width > 100.);
        assert!(narrow.width <= 101.);
        assert!(narrow.height > line.height * 2.);
    }
}
