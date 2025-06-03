using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ScreenTimeTracker.Models;
using System;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Helper class that encapsulates date picker popup functionality
    /// </summary>
    public class DatePickerPopup
    {
        private Popup? _datePickerPopup;
        private Button? _todayButton;
        private Button? _yesterdayButton;
        private Button? _last7DaysButton;
        private readonly Window _owner;
        
        // Events for notifying the owner window about date selection changes
        public event EventHandler<DateTime>? SingleDateSelected;
        public event EventHandler<(DateTime Start, DateTime End)>? DateRangeSelected;
        public event EventHandler? PopupClosed;
        
        // Current state
        private DateTime _selectedDate;
        private DateTime? _selectedEndDate;
        private bool _isDateRangeSelected;

        /// <summary>
        /// Initializes a new instance of the DatePickerPopup class
        /// </summary>
        /// <param name="owner">The owner window that will host this popup</param>
        public DatePickerPopup(Window owner)
        {
            _owner = owner;
            _selectedDate = DateTime.Today;
        }

        /// <summary>
        /// Shows the date picker popup near the specified button
        /// </summary>
        /// <param name="sender">The button that triggered this action</param>
        /// <param name="currentDate">The currently selected date</param>
        /// <param name="currentEndDate">The currently selected end date (for ranges)</param>
        /// <param name="isDateRange">Whether a date range is currently selected</param>
        public void ShowDatePicker(object sender, DateTime currentDate, DateTime? currentEndDate, bool isDateRange)
        {
            _selectedDate = currentDate;
            _selectedEndDate = currentEndDate;
            _isDateRangeSelected = isDateRange;

            // Create the popup if it doesn't exist
            if (_datePickerPopup == null)
            {
                CreateDatePickerPopup();
            }
            else
            {
                // Ensure XamlRoot is set (in case it was lost)
                _datePickerPopup.XamlRoot = _owner.Content.XamlRoot;
                
                // When reopening, update the selected date in the calendar
                if (_datePickerPopup.Child is Grid rootGrid)
                {
                    // Find the calendar and date display
                    var calendar = rootGrid.Children.OfType<CalendarView>().FirstOrDefault();
                    var dateDisplayBorder = rootGrid.Children.OfType<Border>().FirstOrDefault();
                    var dateDisplayText = dateDisplayBorder?.Child as TextBlock;
                    
                    if (calendar != null)
                    {
                        // Clear previous selection
                        calendar.SelectedDates.Clear();
                        
                        // Set the current selected date
                        calendar.SelectedDates.Add(new DateTimeOffset(_selectedDate));
                        
                        // Reset button highlighting
                        ResetQuickSelectButtonStyles();
                        
                        // Update the quick selection button highlighting
                        var today = DateTime.Today;
                        var yesterday = today.AddDays(-1);
                        
                        if (_selectedDate == today && !_isDateRangeSelected)
                        {
                            HighlightQuickSelectButton("Today");
                            if (dateDisplayText != null)
                                dateDisplayText.Text = "Today";
                        }
                        else if (_selectedDate == yesterday && !_isDateRangeSelected)
                        {
                            HighlightQuickSelectButton("Yesterday");
                            if (dateDisplayText != null)
                                dateDisplayText.Text = "Yesterday";
                        }
                        else if (_isDateRangeSelected && _selectedEndDate.HasValue &&
                                 _selectedDate == today.AddDays(-6) && _selectedEndDate.Value == today)
                        {
                            HighlightQuickSelectButton("Last 7 Days");
                            if (dateDisplayText != null)
                                dateDisplayText.Text = "Last 7 days";
                        }
                        else
                        {
                            // Regular date or unrecognized preset
                            if (dateDisplayText != null)
                            {
                                dateDisplayText.Text = _isDateRangeSelected && _selectedEndDate.HasValue ? 
                                    $"{_selectedDate:MMM dd} - {_selectedEndDate:MMM dd}" : 
                                    _selectedDate.ToString("MMM dd");
                            }
                        }
                    }
                }
            }

            if (_datePickerPopup != null)
            {
                // Position the popup near the button and adjust for screen boundaries
                PositionPopup(sender as Button);
                
                // Show the popup
                _datePickerPopup.IsOpen = true;
            }
        }

        /// <summary>
        /// Positions the popup relative to the button and adjusts for screen boundaries
        /// </summary>
        /// <param name="button">The button that triggered the popup</param>
        private void PositionPopup(Button? button)
        {
            if (button == null || _datePickerPopup == null) return;
            
            try
            {
                // Get the button position
                var transform = button.TransformToVisual(null);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, button.ActualHeight));
                
                // Get window dimensions
                var windowWidth = _owner.Bounds.Width;
                var windowHeight = _owner.Bounds.Height;
                
                // Default popup position
                double horizontalOffset = point.X;
                double verticalOffset = point.Y;
                
                // Popup dimensions
                const double popupWidth = 350;
                const double popupHeight = 580;
                
                // Adjust horizontal position if needed
                if (horizontalOffset + popupWidth > windowWidth - 20) // 20px safety margin
                {
                    // If popup would go beyond right edge, align it to the right
                    horizontalOffset = windowWidth - popupWidth - 20;
                }
                
                // Adjust vertical position if needed
                if (verticalOffset + popupHeight > windowHeight - 20) // 20px safety margin
                {
                    // If popup would go beyond bottom edge, show it above the button
                    verticalOffset = point.Y - popupHeight - button.ActualHeight;
                    
                    // If that would put it above the top of the window, just align to top with margin
                    if (verticalOffset < 20)
                    {
                        verticalOffset = 20;
                    }
                }
                
                // Set the position of the popup
                _datePickerPopup.HorizontalOffset = horizontalOffset;
                _datePickerPopup.VerticalOffset = verticalOffset;
                
                System.Diagnostics.Debug.WriteLine($"Positioned popup at X:{horizontalOffset}, Y:{verticalOffset}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error positioning popup: {ex.Message}");
                
                // Fallback positioning - just position at the button location
                var fallbackTransform = button.TransformToVisual(null);
                var fallbackPoint = fallbackTransform.TransformPoint(new Windows.Foundation.Point(0, button.ActualHeight));
                _datePickerPopup.HorizontalOffset = fallbackPoint.X;
                _datePickerPopup.VerticalOffset = fallbackPoint.Y;
            }
        }

        /// <summary>
        /// Creates the date picker popup UI
        /// </summary>
        private void CreateDatePickerPopup()
        {
            try
            {
                // Create a new popup
                _datePickerPopup = new Popup();
                
                // Set the XamlRoot property to connect the popup to the UI tree
                _datePickerPopup.XamlRoot = _owner.Content.XamlRoot;
                
                // Enable light dismiss - allows closing by clicking outside
                _datePickerPopup.IsLightDismissEnabled = true;
                
                // Handle the Closed event to notify when popup is dismissed
                _datePickerPopup.Closed += (s, e) => PopupClosed?.Invoke(this, EventArgs.Empty);
                
                // Create the root grid for the popup content
                var rootGrid = new Grid
                {
                    Background = Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] as Brush,
                    BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16),
                    Width = 350, // Increased width to avoid content being cut off
                    MaxHeight = 580 // Increased max height to ensure calendar fits properly
                };
                
                // Set up row definitions
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Quick selection buttons 
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) }); // Spacing
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Date display
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); // Spacing
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Calendar
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // Spacing
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Action buttons
                
                // Create quick selection buttons in a simple horizontal grid layout
                var buttonsGrid = new StackPanel
                { 
                    Orientation = Orientation.Vertical,
                    Spacing = 8
                };
                Grid.SetRow(buttonsGrid, 0);
                
                // Create first row of buttons (Today, Yesterday)
                var topButtonsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                topButtonsRow.HorizontalAlignment = HorizontalAlignment.Stretch; // Make sure the row takes full width
                
                // Create column definitions for the button row to ensure equal widths
                var todayColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                var yesterdayColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                
                // Convert StackPanel to Grid for better layout control
                var topButtonsGrid = new Grid();
                topButtonsGrid.ColumnDefinitions.Add(todayColumn);
                topButtonsGrid.ColumnDefinitions.Add(yesterdayColumn);
                
                // Create Today button
                _todayButton = new Button
                {
                    Content = "Today",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 4, 0),
                    Style = Application.Current.Resources["AccentButtonStyle"] as Style
                };
                _todayButton.Click += QuickSelect_Today_Click;
                Grid.SetColumn(_todayButton, 0);
                topButtonsGrid.Children.Add(_todayButton);
                
                // Create Yesterday button
                _yesterdayButton = new Button
                {
                    Content = "Yesterday",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                _yesterdayButton.Click += QuickSelect_Yesterday_Click;
                Grid.SetColumn(_yesterdayButton, 1);
                topButtonsGrid.Children.Add(_yesterdayButton);
                
                buttonsGrid.Children.Add(topButtonsGrid);
                
                // Create second row with just Last 7 days button
                _last7DaysButton = new Button
                {
                    Content = "Last 7 days",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 0) // Even margin
                };
                _last7DaysButton.Click += QuickSelect_Last7Days_Click;
                buttonsGrid.Children.Add(_last7DaysButton);
                
                rootGrid.Children.Add(buttonsGrid);
                
                // Create date display text block to show selected date/range
                var dateDisplayBorder = new Border
                {
                    Background = Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"] as Brush,
                    BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Grid.SetRow(dateDisplayBorder, 2);
                
                var dateText = new TextBlock
                {
                    Text = "Today",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style
                };
                dateDisplayBorder.Child = dateText;
                rootGrid.Children.Add(dateDisplayBorder);
                
                // Create calendar view
                var calendar = new CalendarView
                {
                    SelectionMode = CalendarViewSelectionMode.Single,
                    FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    MaxDate = DateTimeOffset.Now, // Can't select future dates
                    MinHeight = 320, // Set minimum height to ensure calendar isn't too small
                    Margin = new Thickness(0, 0, 0, 0), // Even margin
                    CalendarIdentifier = "GregorianCalendar", // Explicitly set calendar type
                    IsGroupLabelVisible = true, // Show month/year label
                    IsTodayHighlighted = true // Highlight today's date
                };
                calendar.SelectedDatesChanged += Calendar_SelectedDatesChanged;
                calendar.CalendarViewDayItemChanging += Calendar_DayItemChanging;
                Grid.SetRow(calendar, 4);
                rootGrid.Children.Add(calendar);
                
                // Create action buttons (Cancel/Done)
                var actionButtonsGrid = new Grid();
                Grid.SetRow(actionButtonsGrid, 6);
                actionButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                actionButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                
                var cancelButton = new Button
                {
                    Content = "Cancel",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                cancelButton.Click += DatePickerCancel_Click;
                Grid.SetColumn(cancelButton, 0);
                actionButtonsGrid.Children.Add(cancelButton);
                
                var doneButton = new Button
                {
                    Content = "Done",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = Application.Current.Resources["AccentButtonStyle"] as Style
                };
                doneButton.Click += DatePickerDone_Click;
                Grid.SetColumn(doneButton, 1);
                actionButtonsGrid.Children.Add(doneButton);
                
                rootGrid.Children.Add(actionButtonsGrid);
                
                // Set the popup content
                _datePickerPopup.Child = rootGrid;
                
                // Initialize date selection
                var today = DateTime.Today;
                calendar.SelectedDates.Add(new DateTimeOffset(today));
                dateText.Text = "Today";
                _selectedDate = today;
                _isDateRangeSelected = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating date picker popup: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the DayItemChanging event to customize calendar day items
        /// </summary>
        private void Calendar_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
        {
            // In WinUI 3, we let the CalendarView handle the styling
        }

        /// <summary>
        /// Handles date selection changes in the calendar
        /// </summary>
        private void Calendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            try
            {
                // Reset button highlighting when manually selecting dates
                ResetQuickSelectButtonStyles();
                
                if (sender.SelectedDates.Count > 0)
                {
                    // Get the selected date
                    _selectedDate = sender.SelectedDates[0].DateTime;
                    _isDateRangeSelected = false;
                    
                    // Update the date display
                    if (_datePickerPopup?.Child is Grid rootGrid)
                    {
                        var dateDisplayBorder = rootGrid.Children.OfType<Border>().FirstOrDefault();
                        var dateText = dateDisplayBorder?.Child as TextBlock;
                        
                        if (dateText != null)
                        {
                            var today = DateTime.Today;
                            
                            if (_selectedDate == today)
                            {
                                dateText.Text = "Today";
                                HighlightQuickSelectButton("Today");
                            }
                            else if (_selectedDate == today.AddDays(-1))
                            {
                                dateText.Text = "Yesterday";
                                HighlightQuickSelectButton("Yesterday");
                            }
                            else
                            {
                                dateText.Text = _selectedDate.ToString("MMM dd, yyyy");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in Calendar_SelectedDatesChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Closes the date picker popup
        /// </summary>
        public void ClosePopup()
        {
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// Highlights a quick select button based on its content
        /// </summary>
        private void HighlightQuickSelectButton(string buttonContent)
        {
            try
            {
                // Reset all buttons first
                ResetQuickSelectButtonStyles();
                
                // Find the button by its content and highlight it
                if (buttonContent == "Today" && _todayButton != null)
                {
                    _todayButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
                }
                else if (buttonContent == "Yesterday" && _yesterdayButton != null)
                {
                    _yesterdayButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
                }
                else if (buttonContent == "Last 7 Days" && _last7DaysButton != null)
                {
                    _last7DaysButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error highlighting button: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the styles of all quick select buttons
        /// </summary>
        private void ResetQuickSelectButtonStyles()
        {
            try
            {
                if (_todayButton != null)
                    _todayButton.Style = Application.Current.Resources["DefaultButtonStyle"] as Style;
                
                if (_yesterdayButton != null)
                    _yesterdayButton.Style = Application.Current.Resources["DefaultButtonStyle"] as Style;
                
                if (_last7DaysButton != null)
                    _last7DaysButton.Style = Application.Current.Resources["DefaultButtonStyle"] as Style;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error resetting button styles: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for Today quick select button
        /// </summary>
        private void QuickSelect_Today_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var today = DateTime.Today;
                _selectedDate = today;
                _isDateRangeSelected = false;
                _selectedEndDate = null;
                
                // Update button styles
                HighlightQuickSelectButton("Today");
                
                // Update calendar selection
                if (_datePickerPopup?.Child is Grid rootGrid)
                {
                    var calendar = rootGrid.Children.OfType<CalendarView>().FirstOrDefault();
                    var dateDisplayBorder = rootGrid.Children.OfType<Border>().FirstOrDefault();
                    var dateText = dateDisplayBorder?.Child as TextBlock;
                    
                    if (calendar != null)
                    {
                        calendar.SelectedDates.Clear();
                        calendar.SelectedDates.Add(new DateTimeOffset(today));
                    }
                    
                    if (dateText != null)
                    {
                        dateText.Text = "Today";
                    }
                }
                
                // Apply the selection immediately (optional)
                SingleDateSelected?.Invoke(this, today);
                ClosePopup();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in QuickSelect_Today_Click: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for Yesterday quick select button
        /// </summary>
        private void QuickSelect_Yesterday_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var yesterday = DateTime.Today.AddDays(-1);
                _selectedDate = yesterday;
                _isDateRangeSelected = false;
                _selectedEndDate = null;
                
                // Update button styles
                HighlightQuickSelectButton("Yesterday");
                
                // Update calendar selection
                if (_datePickerPopup?.Child is Grid rootGrid)
                {
                    var calendar = rootGrid.Children.OfType<CalendarView>().FirstOrDefault();
                    var dateDisplayBorder = rootGrid.Children.OfType<Border>().FirstOrDefault();
                    var dateText = dateDisplayBorder?.Child as TextBlock;
                    
                    if (calendar != null)
                    {
                        calendar.SelectedDates.Clear();
                        calendar.SelectedDates.Add(new DateTimeOffset(yesterday));
                    }
                    
                    if (dateText != null)
                    {
                        dateText.Text = "Yesterday";
                    }
                }
                
                // Apply the selection immediately (optional)
                SingleDateSelected?.Invoke(this, yesterday);
                ClosePopup();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in QuickSelect_Yesterday_Click: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for Last 7 Days quick select button
        /// </summary>
        private void QuickSelect_Last7Days_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var today = DateTime.Today;
                var lastWeek = today.AddDays(-6); // 7 days including today
                
                // Extra validation to ensure dates are valid
                if (lastWeek > today)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Invalid date range calculation - lastWeek ({lastWeek:yyyy-MM-dd}) is after today ({today:yyyy-MM-dd})");
                    lastWeek = today.AddDays(-1); // Fallback to yesterday if calculation is wrong
                }
                
                _selectedDate = lastWeek;
                _selectedEndDate = today;
                _isDateRangeSelected = true;
                
                // Update button styles
                HighlightQuickSelectButton("Last 7 Days");
                
                // Update calendar selection and date display
                if (_datePickerPopup?.Child is Grid rootGrid)
                {
                    var calendar = rootGrid.Children.OfType<CalendarView>().FirstOrDefault();
                    var dateDisplayBorder = rootGrid.Children.OfType<Border>().FirstOrDefault();
                    var dateText = dateDisplayBorder?.Child as TextBlock;
                    
                    if (calendar != null)
                    {
                        try
                        {
                            // WinUI 3 Calendar can't show range selection visually,
                            // so we just select the start date
                            calendar.SelectedDates.Clear();
                            calendar.SelectedDates.Add(new DateTimeOffset(lastWeek));
                        }
                        catch (Exception calEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error updating calendar selection: {calEx.Message}");
                        }
                    }
                    
                    if (dateText != null)
                    {
                        dateText.Text = "Last 7 days";
                    }
                }
                
                // Apply the selection immediately
                DateRangeSelected?.Invoke(this, (lastWeek, today));
                ClosePopup();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in QuickSelect_Last7Days_Click: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                
                // Recover from error - close popup without changing selection
                ClosePopup();
            }
        }

        /// <summary>
        /// Handler for Cancel button click
        /// </summary>
        private void DatePickerCancel_Click(object sender, RoutedEventArgs e)
        {
            // Close the popup without applying changes
            ClosePopup();
            PopupClosed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Handler for Done button click
        /// </summary>
        private void DatePickerDone_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Apply the current selection and close the popup
                if (_isDateRangeSelected && _selectedEndDate.HasValue)
                {
                    DateRangeSelected?.Invoke(this, (_selectedDate, _selectedEndDate.Value));
                }
                else
                {
                    SingleDateSelected?.Invoke(this, _selectedDate);
                }
                
                ClosePopup();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DatePickerDone_Click: {ex.Message}");
            }
        }
    }
} 