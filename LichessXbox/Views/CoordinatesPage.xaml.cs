using System;
using LichessXbox.Chess;
using LichessXbox.Helpers;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    /// <summary>Classic coordinate trainer: name the square, beat the clock.</summary>
    public sealed partial class CoordinatesPage : Page
    {
        readonly Button[] _cells = new Button[64];
        readonly DispatcherTimer _timer = new DispatcherTimer();
        readonly Random _rng = new Random();
        int _target = -1;
        int _score;
        int _timeLeft;
        bool _playing;

        public CoordinatesPage()
        {
            this.InitializeComponent();
            BuildGrid();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Tick;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

        void BuildGrid()
        {
            for (int i = 0; i < 8; i++)
            {
                CoordGrid.RowDefinitions.Add(new RowDefinition());
                CoordGrid.ColumnDefinitions.Add(new ColumnDefinition());
            }
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    int rank = 7 - row, file = col;
                    int sq = rank * 8 + file;
                    bool isLight = (file + rank) % 2 == 1;
                    var btn = new Button
                    {
                        Background = new SolidColorBrush(isLight ? BoardTheme.Light : BoardTheme.Dark),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Tag = sq,
                    };
                    btn.Click += Cell_Click;
                    Grid.SetRow(btn, row);
                    Grid.SetColumn(btn, col);
                    CoordGrid.Children.Add(btn);
                    _cells[sq] = btn;
                }
        }

        void Start_Click(object sender, RoutedEventArgs e)
        {
            _score = 0;
            _timeLeft = 30;
            _playing = true;
            ScoreText.Text = "0";
            TimeText.Text = "30";
            StartButton.Content = "Restart";
            NextTarget();
            _timer.Start();
        }

        void Cell_Click(object sender, RoutedEventArgs e)
        {
            if (!_playing) return;
            int sq = (int)((Button)sender).Tag;
            if (sq == _target)
            {
                _score++;
                ScoreText.Text = _score.ToString();
                Flash(sq, Color.FromArgb(0xAA, 0x6F, 0xA6, 0x30));
                NextTarget();
            }
            else
            {
                Flash(sq, Color.FromArgb(0xAA, 0xD6, 0x3B, 0x2F));
            }
        }

        async void Flash(int sq, Color color)
        {
            int file = ChessMove.FileOf(sq), rank = ChessMove.RankOf(sq);
            bool isLight = (file + rank) % 2 == 1;
            var baseColor = isLight ? BoardTheme.Light : BoardTheme.Dark;
            _cells[sq].Background = new SolidColorBrush(color);
            await System.Threading.Tasks.Task.Delay(180);
            _cells[sq].Background = new SolidColorBrush(baseColor);
        }

        void NextTarget()
        {
            _target = _rng.Next(0, 64);
            TargetText.Text = ChessMove.SquareName(_target);
        }

        void Tick(object sender, object e)
        {
            _timeLeft--;
            TimeText.Text = _timeLeft.ToString();
            if (_timeLeft <= 0)
            {
                _timer.Stop();
                _playing = false;
                TargetText.Text = _score.ToString();
                StartButton.Content = "Play again";
            }
        }
    }
}
