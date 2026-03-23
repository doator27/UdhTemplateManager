using System;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using HardwareTemplateBuilder.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Checks active jobs whose hardware items have no linked templates and whose
/// creation date is older than one week, then sends a single summary email
/// (if SMTP is configured) and stamps <c>MissingTemplateNotifiedAt</c> on each job.
/// </summary>
public class MissingTemplateAlertService
{
    private readonly Func<AppDbContext> _contextFactory;

    /// <summary>Initializes a new instance with the given context factory.</summary>
    public MissingTemplateAlertService(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Runs the alert check. Safe to call at startup; returns immediately if SMTP
    /// is not configured or if there are no qualifying jobs.
    /// </summary>
    public void RunCheck()
    {
        using var ctx = _contextFactory();

        var smtpHost = ctx.AppSettings.FirstOrDefault(s => s.Key == "SmtpHost")?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(smtpHost)) return;

        var smtpPortStr  = ctx.AppSettings.FirstOrDefault(s => s.Key == "SmtpPort")?.Value ?? "587";
        var smtpUser     = ctx.AppSettings.FirstOrDefault(s => s.Key == "SmtpUsername")?.Value ?? string.Empty;
        var smtpPass     = ctx.AppSettings.FirstOrDefault(s => s.Key == "SmtpPassword")?.Value ?? string.Empty;
        var alertTo      = ctx.AppSettings.FirstOrDefault(s => s.Key == "AlertEmailTo")?.Value ?? string.Empty;
        var alertFrom    = ctx.AppSettings.FirstOrDefault(s => s.Key == "AlertEmailFrom")?.Value ?? string.Empty;

        if (string.IsNullOrWhiteSpace(alertTo) || string.IsNullOrWhiteSpace(alertFrom)) return;

        var cutoff = DateTime.UtcNow.AddDays(-7);

        // Jobs that are active, created > 7 days ago, and not yet notified.
        var candidateJobs = ctx.Jobs
            .Where(j => !j.IsComplete && j.CreatedAt <= cutoff && j.MissingTemplateNotifiedAt == null)
            .Include(j => j.JobHardwareLinks)
                .ThenInclude(jh => jh.HardwareItem)
                    .ThenInclude(h => h.HardwareItemTemplates)
            .ToList();

        if (candidateJobs.Count == 0) return;

        var alertLines = new StringBuilder();
        var jobsToMark = new System.Collections.Generic.List<int>();

        foreach (var job in candidateJobs)
        {
            var noTemplateItems = job.JobHardwareLinks
                .Where(jh => jh.HardwareItem?.HardwareItemTemplates.Count == 0)
                .Select(jh => jh.HardwareItem?.ModelNumber ?? $"ID {jh.HardwareItemId}")
                .ToList();

            if (noTemplateItems.Count == 0) continue;

            alertLines.AppendLine($"Job {job.JobNumber} — {job.JobName}:");
            foreach (var item in noTemplateItems)
                alertLines.AppendLine($"  • {item}");
            alertLines.AppendLine();
            jobsToMark.Add(job.Id);
        }

        if (jobsToMark.Count == 0) return;

        // Send email.
        try
        {
            if (!int.TryParse(smtpPortStr, out var port)) port = 587;

            using var client = new SmtpClient(smtpHost, port)
            {
                EnableSsl            = true,
                DeliveryMethod       = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false
            };

            if (!string.IsNullOrWhiteSpace(smtpUser))
                client.Credentials = new NetworkCredential(smtpUser, smtpPass);

            var body = "The following hardware items have been linked to jobs for more than one week "
                     + "but have no associated templates:\n\n"
                     + alertLines.ToString();

            client.Send(new MailMessage(alertFrom, alertTo,
                "Hardware Template Builder — Missing Templates Alert", body));
        }
        catch (Exception)
        {
            // Best-effort; swallow send errors to avoid blocking startup.
            return;
        }

        // Stamp notification time.
        var now = DateTime.UtcNow;
        foreach (var job in candidateJobs.Where(j => jobsToMark.Contains(j.Id)))
            job.MissingTemplateNotifiedAt = now;

        ctx.SaveChanges();
    }
}
