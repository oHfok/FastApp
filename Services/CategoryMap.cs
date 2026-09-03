using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// Which category an application belongs to.
    ///
    /// There are two places a category can be written down and they do not
    /// agree: AppCategories, which is what the dashboard edits and therefore
    /// what anyone has actually curated, and ManagedApps.Category, which is set
    /// once when an app is added and usually still says "Other". The rule is
    /// that the mapping wins and the managed row is only a fallback for an app
    /// nobody has categorised.
    ///
    /// This lived inside the dashboard server, so the desktop palette read
    /// ManagedApps.Category directly and disagreed with the dashboard about
    /// half the list: Claude showed as Other beside Development, Spotify as
    /// Other beside Music.
    /// </summary>
    public static class CategoryMap
    {
        public const string Fallback = "Other";

        public static Dictionary<string, string> Build(AppDbContext db)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (db == null) return map;

            foreach (var mapping in db.AppCategories.AsNoTracking().ToList())
            {
                if (!string.IsNullOrEmpty(mapping.AppName))
                    map[mapping.AppName] = mapping.Category ?? Fallback;
            }

            foreach (var app in db.ManagedApps.AsNoTracking().ToList())
            {
                if (!string.IsNullOrEmpty(app.Name) && !map.ContainsKey(app.Name))
                    map[app.Name] = app.Category ?? Fallback;
            }

            return map;
        }

        /// <summary>
        /// Its own short-lived context, for callers that have no context of
        /// their own and must not borrow the tracker's.
        /// </summary>
        public static Dictionary<string, string> Build()
        {
            try
            {
                using var db = new AppDbContext();
                return Build(db);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static string For(Dictionary<string, string> map, string appName) =>
            map != null && appName != null && map.TryGetValue(appName, out var category)
            && !string.IsNullOrWhiteSpace(category)
                ? category
                : Fallback;
    }
}
