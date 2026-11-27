using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace ScreenTimeTracker.Helpers
{
    public class DatePickerPopup
    {
        private const double PopupWidth = 510;

        private readonly Window _owner;
        private readonly List<(Button Button, Func<DateTime, Selection> GetSelection)> _presets = new();
        private (Popup Popup, CalendarView Calendar, TextBlock Caption)? _view;
        private Selection _draft;

        public event EventHandler<DateTime>? SingleDateSelected;
        public event EventHandler<(DateTime Start, DateTime End)>? DateRangeSelected;
        public event EventHandler? PopupClosed;

        private readonly record struct Selection(DateTime Start, DateTime? End = null);

        public DatePickerPopup(Window owner)
        {
            _owner = owner;
        }

        public void ShowDatePicker(object sender, DateTime currentDate, DateTime? currentEndDate, bool isDateRange)
        {
            if (sender is not Button anchor)
                return;

            _view ??= CreatePopup();
            var (popup, calendar, caption) = _view.Value;
            _draft = new Selection(currentDate, isDateRange ? currentEndDate : null);

            var date = new DateTimeOffset(_draft.Start);
            calendar.SelectedDatesChanged -= CalendarSelectedDatesChanged;
            try
            {
                var today = new DateTimeOffset(DateTime.Today);
                if (calendar.MaxDate != today)
                    calendar.MaxDate = today;
                calendar.SelectedDates.Clear();
                calendar.SelectedDates.Add(date);
                calendar.SetDisplayDate(date);
            }
            finally
            {
                calendar.SelectedDatesChanged += CalendarSelectedDatesChanged;
            }

            RenderSelection(caption, "MMM dd");
            popup.XamlRoot = _owner.Content.XamlRoot;
            var belowAnchor = anchor.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, anchor.ActualHeight));
            popup.HorizontalOffset = Math.Min(belowAnchor.X, _owner.Bounds.Width - PopupWidth - 20);
            popup.VerticalOffset = belowAnchor.Y + 12;
            popup.IsOpen = true;
        }

        private (Popup, CalendarView, TextBlock) CreatePopup()
        {
            var resources = Application.Current.Resources;
            var root = new Grid
            {
                Background = resources["ApplicationPageBackgroundThemeBrush"] as Brush,
                BorderBrush = resources["CardStrokeColorDefaultBrush"] as Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(13.6, 16, 13.6, 16),
                Width = PopupWidth,
                MaxHeight = 540,
                ColumnSpacing = 10.2,
                RowSpacing = 8
            };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(127.5) });
            for (int i = 0; i < 3; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var popup = new Popup
            {
                IsLightDismissEnabled = true,
                Child = new ScrollViewer
                {
                    Content = root,
                    VerticalScrollMode = ScrollMode.Enabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            };
            popup.Closed += (_, _) => PopupClosed?.Invoke(this, EventArgs.Empty);

            var presets = new StackPanel { Spacing = 4 };
            Grid.SetColumn(presets, 1);
            root.Children.Add(presets);
            AddPreset("Today", today => new Selection(today));
            AddPreset("Yesterday", today => new Selection(today.AddDays(-1)));
            AddPreset("Last 7 days", today => new Selection(today.AddDays(-6), today));
            AddPreset("Last 30 days", today => new Selection(today.AddDays(-29), today));
            AddPreset("This month", today => new Selection(new DateTime(today.Year, today.Month, 1), today));

            var calendar = new CalendarView
            {
                SelectionMode = CalendarViewSelectionMode.Single,
                FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 320,
                CalendarIdentifier = "GregorianCalendar",
                IsGroupLabelVisible = true,
                IsTodayHighlighted = true
            };
            calendar.SelectedDatesChanged += CalendarSelectedDatesChanged;
            root.Children.Add(calendar);

            var caption = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = resources["BodyStrongTextBlockStyle"] as Style
            };
            var captionBorder = new Border
            {
                Background = resources["CardBackgroundFillColorSecondaryBrush"] as Brush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Child = caption
            };
            Grid.SetRow(captionBorder, 1);
            Grid.SetColumnSpan(captionBorder, 2);
            root.Children.Add(captionBorder);

            var actions = new Grid { ColumnSpacing = 8 };
            Grid.SetRow(actions, 2);
            Grid.SetColumnSpan(actions, 2);
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch };
            cancel.Click += (_, _) => popup.IsOpen = false;
            actions.Children.Add(cancel);

            var done = new Button
            {
                Content = "Done",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = resources["AccentButtonStyle"] as Style
            };
            done.Click += (_, _) => Apply(popup, _draft);
            Grid.SetColumn(done, 1);
            actions.Children.Add(done);
            root.Children.Add(actions);

            return (popup, calendar, caption);

            void AddPreset(string label, Func<DateTime, Selection> getSelection)
            {
                var button = new Button
                {
                    Content = label,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                button.Click += (_, _) => Apply(popup, getSelection(DateTime.Today));
                presets.Children.Add(button);
                _presets.Add((button, getSelection));
            }
        }

        private void CalendarSelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (args.AddedDates.Count == 0 || _view is not { } view)
                return;

            _draft = new Selection(args.AddedDates[0].Date);
            RenderSelection(view.Caption, "MMM dd, yyyy");
        }

        private void RenderSelection(TextBlock caption, string singleDateFormat)
        {
            var today = DateTime.Today;
            var resources = Application.Current.Resources;
            var accent = resources["AccentButtonStyle"] as Style;
            var normal = resources["DefaultButtonStyle"] as Style;
            string? label = null;
            foreach (var (button, getSelection) in _presets)
            {
                bool selected = label == null && getSelection(today) == _draft;
                button.Style = selected ? accent : normal;
                if (selected)
                    label = (string)button.Content;
            }

            caption.Text = label ?? (_draft.End is DateTime end
                ? $"{_draft.Start:MMM dd} - {end:MMM dd}"
                : _draft.Start.ToString(singleDateFormat));
        }

        private void Apply(Popup popup, Selection selection)
        {
            if (selection.End is DateTime end)
                DateRangeSelected?.Invoke(this, (selection.Start, end));
            else
                SingleDateSelected?.Invoke(this, selection.Start);

            popup.IsOpen = false;
        }
    }
}
