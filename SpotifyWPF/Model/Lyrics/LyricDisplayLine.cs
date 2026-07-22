using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpotifyWPF.Model.Lyrics
{
    /// <summary>One row in the karaoke-style lyrics column.</summary>
    public sealed class LyricDisplayLine : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private bool _isActive;
        private bool _isNear;
        private double _opacity = 0.35;
        private double _fontSize = 13;

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value)
                    return;
                _text = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value)
                    return;
                _isActive = value;
                OnPropertyChanged();
            }
        }

        public bool IsNear
        {
            get => _isNear;
            set
            {
                if (_isNear == value)
                    return;
                _isNear = value;
                OnPropertyChanged();
            }
        }

        public double Opacity
        {
            get => _opacity;
            set
            {
                if (System.Math.Abs(_opacity - value) < 0.001)
                    return;
                _opacity = value;
                OnPropertyChanged();
            }
        }

        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (System.Math.Abs(_fontSize - value) < 0.01)
                    return;
                _fontSize = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
