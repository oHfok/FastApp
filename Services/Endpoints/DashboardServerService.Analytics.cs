using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;

namespace FastApp.Services
{
    // The analytics report.
    //
    // One endpoint, because the page is one report: a partial answer assembled
    // from four requests would let the summary and the insights disagree with
    // each other about which days they were describing.
    //
    // Everything is computed here, on this machine, from a database on this
    // machine. Analytics.AnalyticsEngine has the reasoning; this only serves it.
    public static partial class DashboardServerService
    {
        private static void MapAnalyticsEndpoints(WebApplication app)
        {
            app.MapGet("/api/analytics", async (HttpContext context) =>
            {
                try
                {
                    // Off the request thread: this reads several weeks of
                    // sessions and profiles every day in them, which is tens of
                    // milliseconds rather than one, and the server is running
                    // inside the desktop app.
                    var report = await Task.Run(() =>
                        Analytics.AnalyticsEngine.Build(DateTime.Today));

                    await context.Response.WriteAsJsonAsync(report);
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            // Asking a question about your own activity. Answered here, from
            // the same engine that built the page, and no further than here.
            app.MapGet("/api/analytics/ask", async (HttpContext context) =>
            {
                try
                {
                    string question = context.Request.Query["q"].ToString();

                    var answer = await Task.Run(() =>
                        Analytics.Questions.Ask(question, Analytics.AnalyticsEngine.Facts(DateTime.Today)));

                    await context.Response.WriteAsJsonAsync(new
                    {
                        question,
                        answer.Text,
                        answer.Evidence,
                        answer.BasedOn,
                        answer.Understood,
                        answer.Suggestions
                    });
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });
        }
    }
}
