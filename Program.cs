using System;
using System.Windows;

namespace FastApp
{
    public class Program
    {
        [STAThread] // THIS IS THE MAGIC LINE
        public static void Main(string[] args)
        {
            App app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}