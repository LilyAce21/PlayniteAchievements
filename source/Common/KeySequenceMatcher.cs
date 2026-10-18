using System;
using System.Text;
using System.Windows.Input;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Recognizes a typed sequence of letter keys. Letters A-Z extend the buffer, modifier keys are
    /// ignored, and any other key clears it.
    /// </summary>
    internal sealed class KeySequenceMatcher
    {
        private readonly string _target;
        private readonly StringBuilder _buffer = new StringBuilder();

        public KeySequenceMatcher(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new ArgumentException("The target sequence must not be empty.", nameof(target));
            }

            _target = target.ToUpperInvariant();
        }

        /// <summary>Feeds one key press; true when it completes the sequence, which then clears.</summary>
        public bool Push(Key key)
        {
            if (IsModifier(key))
            {
                return false;
            }

            if (key < Key.A || key > Key.Z)
            {
                _buffer.Clear();
                return false;
            }

            _buffer.Append((char)('A' + (key - Key.A)));
            if (_buffer.Length > _target.Length)
            {
                _buffer.Remove(0, _buffer.Length - _target.Length);
            }

            if (_buffer.Length == _target.Length && _buffer.ToString() == _target)
            {
                _buffer.Clear();
                return true;
            }

            return false;
        }

        private static bool IsModifier(Key key)
        {
            switch (key)
            {
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.CapsLock:
                    return true;
                default:
                    return false;
            }
        }
    }
}
