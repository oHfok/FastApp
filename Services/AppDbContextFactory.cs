using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FastApp.Services
{
    // Used only by design-time tooling (dotnet ef migrations / database update).
    //
    // This used to build a DbContextOptionsBuilder pointing at
    // %LocalAppData%\FastApp\appmanager.db and then return new AppDbContext()
    // anyway -- discarding the options entirely. It happened to behave, because
    // the parameterless constructor routes through OnConfiguring, which has the
    // correct path. But the comment claimed it matched OnConfiguring when it did
    // not, it recreated the collision-prone FastApp folder on every run (the one
    // Velopack owns, and that took the database with it on 2026-08-19), and
    // anyone "fixing" it by actually passing those options would have silently
    // repointed migrations at the wrong database.
    //
    // Deferring to the same constructor the app uses means there is exactly one
    // definition of where the database lives -- AppDbContext.OnConfiguring.
    public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args) => new AppDbContext();
    }
}
