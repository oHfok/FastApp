using FastApp.ViewModels;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;


namespace FastApp.Services
{
    public class AppDbContext : DbContext
    {
        // This represents your actual Database Table
        public DbSet<AppItemModel> ManagedApps { get; set; }

        public DbSet<DailyUsageLog> DailyLogs { get; set; }

        public DbSet<HiddenApp> HiddenApps { get; set; }
        public string DbPath { get; }

        public DbSet<AppCategoryMapping> AppCategories { get; set; }

        public DbSet<SessionLog> SessionLogs { get; set; }
        public DbSet<MacroEventLog> MacroEventLogs { get; set; }

        public AppDbContext()
        {
            // Find the user's hidden AppData/Local folder
            var folder = Environment.SpecialFolder.LocalApplicationData;
            var path = Environment.GetFolderPath(folder);

            // Create a specific folder just for our app: AppData/Local/NexusAppManager
            var appFolder = Path.Combine(path, "NexusAppManager");
            Directory.CreateDirectory(appFolder);

            // Define the database file name
            DbPath = Path.Combine(appFolder, "appmanager.db");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. Index SessionLog by StartTime (Crucial for Dashboard timeframe queries)
            modelBuilder.Entity<SessionLog>()
                .HasIndex(s => s.StartTime);

            // 2. Composite Index (for querying specific apps within a specific timeframe)
            modelBuilder.Entity<SessionLog>()
                .HasIndex(s => new { s.AppName, s.StartTime });

            // 3. Index Macro logs by Timestamp
            modelBuilder.Entity<MacroEventLog>()
                .HasIndex(m => m.Timestamp);
        }

        // Tell Entity Framework to use SQLite and point it to our file
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // 1. Find the safe Windows AppData/Local folder
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 2. Create a dedicated folder for your app
            string folder = System.IO.Path.Combine(appData, "FastApp");
            System.IO.Directory.CreateDirectory(folder);

            // 3. Point the database there!
            string dbPath = System.IO.Path.Combine(folder, "appmanager.db");

            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        public class HiddenApp
        {
            public int Id { get; set; }
            public string AppName { get; set; }
        }

        public class AppCategoryMapping
        {
            [System.ComponentModel.DataAnnotations.Key]
            public string AppName { get; set; }
            public string Category { get; set; }
        }
    }
}