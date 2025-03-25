using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ScreenTimeTracker.Services;
using ScreenTimeTracker.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Text;
using LiveChartsCore.SkiaSharpView.WinUI;
using LiveChartsCore.SkiaSharpView;
using Windows.Storage.Pickers;
using Windows.Storage;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.Generic;

namespace ScreenTimeTracker.Views
{
    public sealed partial class DashboardPage : Page
    {
        public DashboardViewModel ViewModel { get; private set; }

        public DashboardPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Get the database service from parameters
            if (e.Parameter is DatabaseService databaseService)
            {
                ViewModel = new DashboardViewModel(databaseService);
                DatePicker.Date = DateTime.Today;
            }
        }

        private void DatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (args.NewDate.HasValue && ViewModel != null)
            {
                ViewModel.SelectedDate = args.NewDate.Value.Date;
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            try
            {
                // Create content for CSV export
                var csvBuilder = new StringBuilder();
                csvBuilder.AppendLine("Application,Duration (hours),Duration (minutes)");

                // Add each app's data
                foreach (var app in ViewModel.TopApps)
                {
                    csvBuilder.AppendLine($"{app.ProcessName},{app.Duration.TotalHours:F2},{app.Duration.TotalMinutes:F2}");
                }

                // Add summary data
                csvBuilder.AppendLine();
                csvBuilder.AppendLine($"Total Screen Time,{ViewModel.TotalScreenTime.TotalHours:F2},{ViewModel.TotalScreenTime.TotalMinutes:F2}");
                csvBuilder.AppendLine($"Date,{ViewModel.SelectedDate:yyyy-MM-dd}");

                // Get a filename to save
                var window = App.Current.Windows.FirstOrDefault();
                if (window == null) return;

                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = $"ScreenTime_{ViewModel.SelectedDate:yyyy-MM-dd}"
                };

                savePicker.FileTypeChoices.Add("CSV File", new List<string>() { ".csv" });

                // Initialize picker with window
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, WinRT.Interop.WindowNative.GetWindowHandle(window));

                StorageFile file = await savePicker.PickSaveFileAsync();
                if (file != null)
                {
                    // Write the CSV data
                    await FileIO.WriteTextAsync(file, csvBuilder.ToString());

                    // Show success message
                    var dialog = new ContentDialog
                    {
                        Title = "Export Complete",
                        Content = $"Data has been exported to {file.Path}",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                // Show error message
                var dialog = new ContentDialog
                {
                    Title = "Export Error",
                    Content = $"An error occurred: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate back to the main page
            if (this.Frame.CanGoBack)
            {
                this.Frame.GoBack();
            }
        }

        private async void MonthlyReportButton_Click(object sender, RoutedEventArgs e)
        {
            // Show a dialog with monthly summary
            try
            {
                // Get the first day of the month
                var firstDayOfMonth = new DateTime(ViewModel.SelectedDate.Year, ViewModel.SelectedDate.Month, 1);
                var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

                // TODO: Implement monthly report data calculation
                // For now, show a placeholder dialog
                var dialog = new ContentDialog
                {
                    Title = $"Monthly Report - {firstDayOfMonth:MMMM yyyy}",
                    Content = $"Detailed monthly reports will be available in a future update.\n\nView data for each day using the date picker, or export your data to CSV for further analysis.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Could not generate monthly report: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        public Axis[] CreateWeeklyXAxis()
        {
            return new Axis[]
            {
                new Axis
                {
                    Labels = ViewModel.WeeklyLabels,
                    LabelsPaint = new SolidColorPaint(SKColors.Gray),
                    LabelsRotation = 0,
                    SeparatorsPaint = new SolidColorPaint(SKColors.LightGray)
                    {
                        StrokeThickness = 1
                    }
                }
            };
        }
    }
} 