using System;
using System.Windows;

namespace orf2Postgres
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        public void InitializeComponent()
        {
            StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        }
    }
} 