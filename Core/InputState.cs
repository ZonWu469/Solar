using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Solar.Core
{
    /// <summary>Per-frame keyboard/mouse snapshot with edge detection.</summary>
    public sealed class InputState
    {
        private KeyboardState _kb, _kbPrev;
        private MouseState _m, _mPrev;

        private readonly StringBuilder _typedAccum = new();
        /// <summary>Printable characters typed since the previous frame (for text fields).</summary>
        public string Typed { get; private set; } = "";

        /// <summary>Wired to Window.TextInput; accumulates printable ASCII for the next frame.</summary>
        public void OnTextInput(char c)
        {
            if (c >= 32 && c < 127) _typedAccum.Append(c);
        }

        public void Update()
        {
            _kbPrev = _kb; _mPrev = _m;
            _kb = Keyboard.GetState();
            _m = Mouse.GetState();
            Typed = _typedAccum.ToString();
            _typedAccum.Clear();
        }

        public bool Down(Keys k) => _kb.IsKeyDown(k);
        public bool Pressed(Keys k) => _kb.IsKeyDown(k) && !_kbPrev.IsKeyDown(k);
        public bool Released(Keys k) => !_kb.IsKeyDown(k) && _kbPrev.IsKeyDown(k);

        public Vector2 MousePos => new Vector2(_m.X, _m.Y);
        public bool LeftDown => _m.LeftButton == ButtonState.Pressed;
        public bool LeftClick => _m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released;
        public bool RightClick => _m.RightButton == ButtonState.Pressed && _mPrev.RightButton == ButtonState.Released;
        public int WheelDelta => _m.ScrollWheelValue - _mPrev.ScrollWheelValue;
    }
}
