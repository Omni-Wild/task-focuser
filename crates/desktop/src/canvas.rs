//! Original pixel artwork and paper decoration, rendered without a continuous redraw loop.

use crate::{
    app::{App, Message, PAD},
    surface::SurfaceRole,
};
use iced::{
    Color, Element, Point, Rectangle, Renderer, Size, Theme, mouse,
    widget::{
        Canvas,
        canvas::{self, Frame, Geometry, Path, Stroke},
    },
};
use task_focuser_core::{
    companion::EffectKind,
    sprites::{self, BirdId},
    vines,
};

pub const INK: Color = Color::from_rgb8(0x2B, 0x2A, 0x25);

pub fn hex(hex: &str) -> Color {
    let rgb = u32::from_str_radix(hex.trim_start_matches('#'), 16).unwrap_or(0xFFF59E);
    Color::from_rgb8((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8)
}
pub fn alpha(mut color: Color, opacity: f32) -> Color {
    color.a *= opacity;
    color
}
pub fn darken(color: Color, factor: f32) -> Color {
    Color {
        r: color.r * (1. - factor),
        g: color.g * (1. - factor),
        b: color.b * (1. - factor),
        ..color
    }
}

pub struct Artwork<'a> {
    pub app: &'a App,
    pub role: SurfaceRole,
}
impl canvas::Program<Message> for Artwork<'_> {
    type State = ();
    fn draw(
        &self,
        _: &(),
        renderer: &Renderer,
        _: &Theme,
        bounds: Rectangle,
        _: mouse::Cursor,
    ) -> Vec<Geometry> {
        let mut frame = Frame::new(renderer, bounds.size());
        let opacity = f32::from(self.app.state().opacity) / 100.;
        match self.role {
            SurfaceRole::Note => {
                let paper = self.app.paper_bounds();
                let origin = Point::new(PAD as f32, PAD as f32);
                let size = Size::new(paper.width as f32, paper.height as f32);
                let opacity = opacity * self.app.note_alpha();
                let color = alpha(hex(&self.app.state().color), opacity);
                frame.fill(
                    &Path::rounded_rectangle(Point::new(origin.x, origin.y + 3.), size, 6.0.into()),
                    Color::from_rgba(0., 0., 0., 0.18 * opacity),
                );
                frame.fill(&Path::rounded_rectangle(origin, size, 6.0.into()), color);
                let strip_height = 6. + 34. * self.app.unfold() as f32;
                let strip = Path::new(|path| {
                    path.move_to(Point::new(origin.x + 6., origin.y));
                    path.line_to(Point::new(origin.x + size.width - 6., origin.y));
                    path.quadratic_curve_to(
                        Point::new(origin.x + size.width, origin.y),
                        Point::new(origin.x + size.width, origin.y + 6.),
                    );
                    path.line_to(Point::new(origin.x + size.width, origin.y + strip_height));
                    path.line_to(Point::new(origin.x, origin.y + strip_height));
                    path.line_to(Point::new(origin.x, origin.y + 6.));
                    path.quadratic_curve_to(origin, Point::new(origin.x + 6., origin.y));
                    path.close();
                });
                frame.fill(&strip, darken(color, 0.12));
                if self.app.vines_extent() > 0. {
                    for (vine, length) in [(vines::BOTTOM, size.width), (vines::RIGHT, size.height)]
                    {
                        for cell in
                            vines::cells(vine, vines::length_for((length / 3.).floor() as i32))
                        {
                            if f64::from(cell.at)
                                > f64::from(vines::length_for((length / 3.).floor() as i32))
                                    * self.app.vines_extent()
                            {
                                continue;
                            }
                            let color = match cell.color {
                                vines::STEM => Color::from_rgb8(0x3E, 0x7D, 0x3E),
                                vines::LEAF => Color::from_rgb8(0x5F, 0xA8, 0x5F),
                                vines::SHINE => Color::from_rgb8(0x8F, 0xD4, 0x8F),
                                _ => Color::from_rgb8(0xFF, 0x9B, 0xC1),
                            };
                            frame.fill_rectangle(
                                Point::new(
                                    origin.x + size.width - (cell.x + 1) as f32 * 3.,
                                    origin.y + size.height - (cell.y + 1) as f32 * 3.,
                                ),
                                Size::new(3., 3.),
                                alpha(color, opacity),
                            );
                        }
                    }
                }
            }
            SurfaceRole::Dot => {
                let armed = self.app.dot_target.is_some();
                let hot = self.app.dot_target.is_some_and(|target| target.hot);
                let radius = (if hot {
                    15.
                } else if armed {
                    12.
                } else {
                    9.
                }) * self.app.dot_scale();
                frame.fill(
                    &Path::circle(
                        Point::new(bounds.width / 2., bounds.height / 2.),
                        radius + 2.,
                    ),
                    Color::from_rgba8(0xD3, 0x2F, 0x2F, if armed { 0.20 } else { 0.12 }),
                );
                frame.fill(
                    &Path::circle(Point::new(bounds.width / 2., bounds.height / 2.), radius),
                    Color::from_rgb8(0xD3, 0x2F, 0x2F),
                );
                frame.stroke(
                    &Path::circle(Point::new(bounds.width / 2., bounds.height / 2.), radius),
                    Stroke::default()
                        .with_color(Color::from_rgb8(0xA8, 0x1E, 0x1E))
                        .with_width(1.),
                );
            }
            SurfaceRole::CloseTarget => {
                let hot = self.app.close_target.is_some_and(|target| target.hot);
                let center = Point::new(bounds.width / 2., bounds.height / 2.);
                frame.fill(
                    &Path::circle(center, if hot { 24. } else { 20. }),
                    if hot {
                        Color::from_rgba8(0xD3, 0x2F, 0x2F, 0.95)
                    } else {
                        Color::from_rgba8(0x3A, 0x3A, 0x3A, 0.90)
                    },
                );
                frame.stroke(
                    &Path::circle(center, if hot { 24. } else { 20. }),
                    Stroke::default().with_color(Color::from_rgba(1., 1., 1., 0.2)),
                );
                let path = Path::new(|p| {
                    p.move_to(Point::new(center.x - 6., center.y - 6.));
                    p.line_to(Point::new(center.x + 6., center.y + 6.));
                    p.move_to(Point::new(center.x + 6., center.y - 6.));
                    p.line_to(Point::new(center.x - 6., center.y + 6.));
                });
                frame.stroke(
                    &path,
                    Stroke::default().with_color(Color::WHITE).with_width(2.4),
                );
            }
            SurfaceRole::Bird => {
                let stage = self
                    .app
                    .surfaces
                    .get(&self.role)
                    .copied()
                    .unwrap_or_default();
                let feet = self.app.companion.position();
                let shift = self.app.companion.sprite_offset();
                if self.app.companion.visible() {
                    draw_bird(
                        &mut frame,
                        self.app.companion.bird(),
                        self.app.companion.frame(),
                        Point::new(
                            (feet.x - 33. + shift.x - stage.x) as f32,
                            (feet.y - 48. + shift.y - stage.y) as f32,
                        ),
                        3.,
                        self.app.companion.facing_left(),
                        self.app.companion.opacity() as f32,
                    );
                }
                for effect in self.app.companion.effects() {
                    let grid: &[&str] = match effect.kind {
                        EffectKind::Heart => &sprites::HEART,
                        EffectKind::Dust => &sprites::DUST,
                        EffectKind::Seed => &sprites::SEED,
                    };
                    draw_grid(
                        &mut frame,
                        grid,
                        Point::new(
                            (effect.position.x - stage.x) as f32,
                            (effect.position.y - stage.y) as f32,
                        ),
                        3.,
                        effect.opacity as f32,
                    );
                }
                if let Some(seed) = self.app.companion.seed_position() {
                    draw_grid(
                        &mut frame,
                        &sprites::SEED,
                        Point::new((seed.x - stage.x) as f32, (seed.y - stage.y) as f32),
                        3.,
                        1.,
                    );
                }
            }
            SurfaceRole::Cucumber => {
                let elapsed = self
                    .app
                    .cucumber_fall
                    .map_or(0., |(_, start)| start.elapsed().as_secs_f32());
                let falling = !self.app.cucumber_dragging();
                let y = if falling {
                    (elapsed / 0.22).min(1.).powi(2) * 24.
                } else {
                    0.
                };
                draw_grid(
                    &mut frame,
                    &sprites::CUCUMBER,
                    Point::new(4., 4. + y),
                    20. / 7.,
                    if falling {
                        1. - (elapsed / 0.22).min(1.)
                    } else {
                        1.
                    },
                );
            }
        }
        vec![frame.into_geometry()]
    }
}

pub fn artwork(app: &App, role: SurfaceRole, width: f32, height: f32) -> Element<'_, Message> {
    Canvas::new(Artwork { app, role })
        .width(width)
        .height(height)
        .into()
}

pub fn draw_bird(
    frame: &mut Frame,
    bird: BirdId,
    pose: sprites::Frame,
    origin: Point,
    cell: f32,
    flipped: bool,
    opacity: f32,
) {
    for pixel in sprites::pixels(bird, pose) {
        let x = if flipped {
            sprites::COLUMNS as f32 - 1. - f32::from(pixel.x)
        } else {
            f32::from(pixel.x)
        };
        frame.fill_rectangle(
            Point::new(origin.x + x * cell, origin.y + f32::from(pixel.y) * cell),
            Size::new(cell, cell),
            alpha(
                Color::from_rgb8(pixel.color[0], pixel.color[1], pixel.color[2]),
                opacity,
            ),
        );
    }
}
fn draw_grid(frame: &mut Frame, grid: &[&str], origin: Point, cell: f32, opacity: f32) {
    for (y, row) in grid.iter().enumerate() {
        for (x, c) in row.bytes().enumerate() {
            if let Some(rgb) = sprites::color(c) {
                frame.fill_rectangle(
                    Point::new(origin.x + x as f32 * cell, origin.y + y as f32 * cell),
                    Size::new(cell, cell),
                    alpha(Color::from_rgb8(rgb[0], rgb[1], rgb[2]), opacity),
                );
            }
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub enum Icon {
    Pinned(bool),
    Opaque(bool),
    Sound(bool),
    Plants(bool),
    Lock,
    Grip,
    Cucumber,
    Pencil,
}
pub struct Glyph {
    pub icon: Icon,
    pub opacity: f32,
}
impl canvas::Program<Message> for Glyph {
    type State = ();
    fn draw(
        &self,
        _: &(),
        renderer: &Renderer,
        _: &Theme,
        bounds: Rectangle,
        _: mouse::Cursor,
    ) -> Vec<Geometry> {
        let mut frame = Frame::new(renderer, bounds.size());
        frame.translate(iced::Vector::new(
            (bounds.width - 14.) / 2.,
            (bounds.height - 14.) / 2.,
        ));
        let ink = alpha(INK, self.opacity);
        let line = |points: &[(f32, f32)]| {
            Path::new(|p| {
                if let Some((first, rest)) = points.split_first() {
                    p.move_to(Point::new(first.0, first.1));
                    for point in rest {
                        p.line_to(Point::new(point.0, point.1));
                    }
                }
            })
        };
        let stroke = Stroke::default().with_color(ink).with_width(1.3);
        match self.icon {
            Icon::Cucumber => draw_grid(
                &mut frame,
                &sprites::CUCUMBER,
                Point::ORIGIN,
                2.,
                self.opacity,
            ),
            Icon::Pinned(false) => {
                frame.stroke(&line(&[(7., 12.5), (7., 2.6)]), stroke);
                frame.stroke(&line(&[(3.2, 6.4), (7., 2.6), (10.8, 6.4)]), stroke);
            }
            Icon::Pinned(true) => {
                frame.fill(&Path::circle(Point::new(7., 4.6), 2.4), ink);
                let nest = Path::new(|p| {
                    p.move_to(Point::new(1., 7.6));
                    p.line_to(Point::new(13., 7.6));
                    p.bezier_curve_to(
                        Point::new(13., 14.6),
                        Point::new(1., 14.6),
                        Point::new(1., 7.6),
                    );
                });
                frame.fill(&nest, ink);
            }
            Icon::Opaque(on) => {
                let bird = Path::new(|p| {
                    p.move_to(Point::new(3., 11.));
                    p.bezier_curve_to(Point::new(0., 11.), Point::new(3., 6.), Point::new(5., 6.));
                    p.line_to(Point::new(5., 3.));
                    p.bezier_curve_to(Point::new(8., 0.), Point::new(11., 2.), Point::new(11., 4.));
                    p.line_to(Point::new(14., 5.));
                    p.line_to(Point::new(11., 6.));
                    p.bezier_curve_to(
                        Point::new(11., 12.),
                        Point::new(8., 13.),
                        Point::new(3., 11.),
                    );
                });
                frame.fill(&bird, alpha(ink, if on { 1. } else { 0.38 }));
            }
            Icon::Sound(on) => {
                frame.fill(
                    &line(&[
                        (1.5, 5.),
                        (4.5, 5.),
                        (8., 2.),
                        (8., 12.),
                        (4.5, 9.),
                        (1.5, 9.),
                        (1.5, 5.),
                    ]),
                    ink,
                );
                if on {
                    for (radius, x) in [(2.5, 10.), (5., 11.5)] {
                        let arc = Path::new(|p| {
                            p.move_to(Point::new(x, 7. - radius));
                            p.quadratic_curve_to(
                                Point::new(x + radius, 7.),
                                Point::new(x, 7. + radius),
                            );
                        });
                        frame.stroke(&arc, stroke);
                    }
                } else {
                    frame.stroke(&line(&[(10., 5.), (13.4, 9.)]), stroke);
                    frame.stroke(&line(&[(13.4, 5.), (10., 9.)]), stroke);
                }
            }
            Icon::Plants(on) => {
                let leaf = Path::new(|p| {
                    p.move_to(Point::new(3., 11.));
                    p.bezier_curve_to(
                        Point::new(3., 5.6),
                        Point::new(6.4, 2.4),
                        Point::new(12.2, 2.2),
                    );
                    p.bezier_curve_to(
                        Point::new(12.2, 7.8),
                        Point::new(8.8, 11.),
                        Point::new(3., 11.),
                    );
                });
                if on {
                    frame.fill(&leaf, ink);
                } else {
                    frame.stroke(&leaf, stroke);
                }
                frame.stroke(&line(&[(1.4, 12.6), (8.6, 5.6)]), stroke);
            }
            Icon::Lock => {
                frame.stroke(
                    &Path::new(|p| {
                        p.move_to(Point::new(4., 6.));
                        p.line_to(Point::new(4., 4.));
                        p.bezier_curve_to(
                            Point::new(4., 0.),
                            Point::new(10., 0.),
                            Point::new(10., 4.),
                        );
                        p.line_to(Point::new(10., 6.));
                    }),
                    stroke,
                );
                frame.stroke(
                    &Path::rounded_rectangle(Point::new(2.5, 6.), Size::new(9., 7.), 1.5.into()),
                    stroke,
                );
                frame.fill(&Path::circle(Point::new(7., 9.), 1.), ink);
            }
            Icon::Grip => {
                for offset in [0., 4., 8.] {
                    frame.stroke(
                        &line(&[(4. + offset, 12.), (12., 4. + offset)]),
                        Stroke::default()
                            .with_color(alpha(ink, 0.45))
                            .with_width(1.),
                    );
                }
            }
            Icon::Pencil => {
                frame.stroke(
                    &line(&[
                        (2.2, 11.8),
                        (2.8, 9.2),
                        (9.6, 2.4),
                        (11.6, 4.4),
                        (4.8, 11.2),
                        (2.2, 11.8),
                    ]),
                    stroke,
                );
                frame.stroke(&line(&[(8.3, 3.7), (10.3, 5.7)]), stroke);
            }
        }
        vec![frame.into_geometry()]
    }
}

pub struct Portrait(pub BirdId);
impl canvas::Program<Message> for Portrait {
    type State = ();
    fn draw(
        &self,
        _: &(),
        renderer: &Renderer,
        _: &Theme,
        bounds: Rectangle,
        _: mouse::Cursor,
    ) -> Vec<Geometry> {
        let mut frame = Frame::new(renderer, bounds.size());
        draw_bird(
            &mut frame,
            self.0,
            sprites::Frame::IdleOpen,
            Point::ORIGIN,
            1.5,
            false,
            1.,
        );
        vec![frame.into_geometry()]
    }
}

/// Applies the original note's render-only squash and lock shake to its native widgets.
pub struct NoteTransform<'a> {
    content: Element<'a, Message>,
    scale: f32,
    anchor: Point,
    shift: f32,
    interactive: bool,
}
impl<'a> NoteTransform<'a> {
    pub fn visual(content: Element<'a, Message>) -> Element<'a, Message> {
        Element::new(Self {
            content,
            scale: 1.,
            anchor: Point::ORIGIN,
            shift: 0.,
            interactive: false,
        })
    }
    pub fn wrap(
        content: Element<'a, Message>,
        scale: f32,
        anchor: Point,
        shift: f32,
    ) -> Element<'a, Message> {
        Element::new(Self {
            content,
            scale,
            anchor,
            shift,
            interactive: true,
        })
    }
}
impl iced::advanced::Widget<Message, Theme, Renderer> for NoteTransform<'_> {
    fn size(&self) -> iced::Size<iced::Length> {
        self.content.as_widget().size()
    }
    fn children(&self) -> Vec<iced::advanced::widget::Tree> {
        vec![iced::advanced::widget::Tree::new(&self.content)]
    }
    fn diff(&self, tree: &mut iced::advanced::widget::Tree) {
        tree.diff_children(std::slice::from_ref(&self.content));
    }
    fn layout(
        &mut self,
        tree: &mut iced::advanced::widget::Tree,
        renderer: &Renderer,
        limits: &iced::advanced::layout::Limits,
    ) -> iced::advanced::layout::Node {
        self.content
            .as_widget_mut()
            .layout(&mut tree.children[0], renderer, limits)
    }
    fn update(
        &mut self,
        tree: &mut iced::advanced::widget::Tree,
        event: &iced::Event,
        layout: iced::advanced::Layout<'_>,
        cursor: mouse::Cursor,
        renderer: &Renderer,
        clipboard: &mut dyn iced::advanced::Clipboard,
        shell: &mut iced::advanced::Shell<'_, Message>,
        viewport: &Rectangle,
    ) {
        if !self.interactive {
            return;
        }
        self.content.as_widget_mut().update(
            &mut tree.children[0],
            event,
            layout,
            cursor,
            renderer,
            clipboard,
            shell,
            viewport,
        );
    }
    fn draw(
        &self,
        tree: &iced::advanced::widget::Tree,
        renderer: &mut Renderer,
        theme: &Theme,
        style: &iced::advanced::renderer::Style,
        layout: iced::advanced::Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
    ) {
        use iced::advanced::Renderer as _;
        let anchor = layout.bounds().position() + iced::Vector::new(self.anchor.x, self.anchor.y);
        let transform = iced::Transformation::translate(anchor.x + self.shift, anchor.y)
            * iced::Transformation::scale(self.scale)
            * iced::Transformation::translate(-anchor.x, -anchor.y);
        renderer.with_transformation(transform, |renderer| {
            self.content.as_widget().draw(
                &tree.children[0],
                renderer,
                theme,
                style,
                layout,
                cursor,
                viewport,
            )
        });
    }
    fn operate(
        &mut self,
        tree: &mut iced::advanced::widget::Tree,
        layout: iced::advanced::Layout<'_>,
        renderer: &Renderer,
        operation: &mut dyn iced::advanced::widget::Operation,
    ) {
        self.content
            .as_widget_mut()
            .operate(&mut tree.children[0], layout, renderer, operation);
    }
    fn mouse_interaction(
        &self,
        tree: &iced::advanced::widget::Tree,
        layout: iced::advanced::Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
        renderer: &Renderer,
    ) -> mouse::Interaction {
        if !self.interactive {
            return mouse::Interaction::None;
        }
        self.content.as_widget().mouse_interaction(
            &tree.children[0],
            layout,
            cursor,
            viewport,
            renderer,
        )
    }
    fn overlay<'b>(
        &'b mut self,
        tree: &'b mut iced::advanced::widget::Tree,
        layout: iced::advanced::Layout<'b>,
        renderer: &Renderer,
        viewport: &Rectangle,
        translation: iced::Vector,
    ) -> Option<iced::advanced::overlay::Element<'b, Message, Theme, Renderer>> {
        if !self.interactive {
            return None;
        }
        self.content.as_widget_mut().overlay(
            &mut tree.children[0],
            layout,
            renderer,
            viewport,
            translation,
        )
    }
}
