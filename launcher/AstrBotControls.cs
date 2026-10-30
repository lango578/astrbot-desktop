// Win11 风格控件：圆角卡片、胶囊徽标、圆角按钮与带焦点描边的输入框。
// 统一用 Region 裁圆 + OnPaint 画边框，避免 WinForms 自带的方形边框。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AstrBotDesktop
{
    internal static class Shape
    {
        public static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            int d = radius * 2;
            if (d <= 0 || rect.Width <= 2 || rect.Height <= 2)
            {
                return new GraphicsPath();
            }
            if (d > rect.Height)
            {
                d = rect.Height;
            }
            if (d > rect.Width)
            {
                d = rect.Width;
            }
            GraphicsPath path = new GraphicsPath();
            Rectangle box = new Rectangle(rect.X, rect.Y, d, d);
            path.AddArc(box, 180f, 90f);
            box.X = rect.Right - d;
            path.AddArc(box, 270f, 90f);
            box.Y = rect.Bottom - d;
            path.AddArc(box, 0f, 90f);
            box.X = rect.X;
            path.AddArc(box, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }

    internal class RoundPanel : Panel
    {
        private int _radius = 8;
        private Color _borderColor = Theme.Line;
        private bool _drawBorder = true;

        public RoundPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public int Radius
        {
            get { return _radius; }
            set { _radius = value; ApplyShape(); Invalidate(); }
        }

        public Color BorderColor
        {
            get { return _borderColor; }
            set { _borderColor = value; Invalidate(); }
        }

        public bool DrawBorder
        {
            get { return _drawBorder; }
            set { _drawBorder = value; Invalidate(); }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyShape();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyShape();
        }

        private void ApplyShape()
        {
            if (!IsHandleCreated || Width < 3 || Height < 3)
            {
                return;
            }
            if (_radius <= 0)
            {
                // An empty GraphicsPath would clip the whole panel away.
                Region = null;
                return;
            }
            using (GraphicsPath path = Shape.Rounded(new Rectangle(0, 0, Width, Height), _radius))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush fill = new SolidBrush(BackColor))
            {
                e.Graphics.FillRectangle(fill, ClientRectangle);
            }
            if (!_drawBorder)
            {
                return;
            }
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, _radius))
            using (Pen pen = new Pen(_borderColor, 1f))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal class RoundButton : Button
    {
        private int _radius = 6;
        private Color _rest;
        private Color _hover;
        private Color _press;
        private bool _active;
        private Color _textColor = Color.White;

        public RoundButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public int Radius
        {
            get { return _radius; }
            set { _radius = value; ApplyShape(); }
        }

        public void UsePalette(Color rest, Color hover, Color press, Color textColor)
        {
            _rest = rest;
            _hover = hover;
            _press = press;
            _textColor = textColor;
            BackColor = rest;
            ForeColor = textColor;
        }

        public override Color BackColor
        {
            get { return base.BackColor; }
            set { base.BackColor = value; }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyShape();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyShape();
        }

        private void ApplyShape()
        {
            if (!IsHandleCreated || Width < 5 || Height < 5)
            {
                return;
            }
            using (GraphicsPath path = Shape.Rounded(new Rectangle(0, 0, Width, Height), _radius))
            {
                Region = new Region(path);
            }
        }

        public bool Active
        {
            get { return _active; }
            set
            {
                _active = value;
                ForeColor = value ? Color.White : _textColor;
                BackColor = value ? _press : _rest;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_active && _hover.ToArgb() != 0)
            {
                BackColor = _hover;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            BackColor = _active ? _press : _rest;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_active && _press.ToArgb() != 0)
            {
                BackColor = _press;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_active)
            {
                BackColor = _press;
                return;
            }
            BackColor = ClientRectangle.Contains(e.Location) ? _hover : _rest;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, _radius))
            using (SolidBrush brush = new SolidBrush(BackColor))
            {
                g.FillPath(brush, path);
                if (FlatAppearance.BorderSize > 0)
                {
                    using (Pen pen = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis);
        }
    }

    // Win11 的输入框：圆角、常态细边框、聚焦时换成强调色边框。
    internal class InputBox : RoundPanel
    {
        private readonly TextBox _box;

        public InputBox()
        {
            Radius = 5;
            Height = 30;
            BackColor = Theme.Card;
            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Theme.Card;
            _box.ForeColor = Theme.Text;
            _box.Font = Theme.Body;
            _box.Margin = new Padding(0);
            _box.Enter += delegate { BorderColor = Theme.Accent; Invalidate(); };
            _box.Leave += delegate { BorderColor = Theme.Line; Invalidate(); };
            _box.SizeChanged += delegate { LayoutBox(); };
            Controls.Add(_box);
            LayoutBox();
        }

        public TextBox Box
        {
            get { return _box; }
        }

        public new string Text
        {
            get { return _box.Text; }
            set { _box.Text = value; }
        }

        public override Font Font
        {
            get { return base.Font; }
            set
            {
                base.Font = value;
                _box.Font = value;
                LayoutBox();
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutBox();
        }

        private void LayoutBox()
        {
            if (_box == null || !IsHandleCreated)
            {
                return;
            }
            _box.Location = new Point(9, Math.Max(4, (Height - _box.PreferredHeight) / 2));
            _box.Width = Math.Max(10, Width - 18);
        }
    }

    internal class RoundComboBox : ComboBox
    {
        public RoundComboBox()
        {
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            ItemHeight = 26;
            DrawItem += OnDrawItem;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!IsHandleCreated || Width < 5 || Height < 5)
            {
                return;
            }
            using (GraphicsPath path = Shape.Rounded(new Rectangle(0, 0, Width, Height), 5))
            {
                Region = new Region(path);
            }
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                return;
            }
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = selected ? Theme.AccentDark : Theme.Card;
            using (SolidBrush brush = new SolidBrush(back))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            string text = GetItemText(Items[e.Index]);
            TextRenderer.DrawText(e.Graphics, text, Font, e.Bounds, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
