using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 5 - Validate Story updates using a Pre Image.
    ///
    /// Registration:
    ///   Table      : Story (sm_story)
    ///   Message    : Update
    ///   Stage      : Pre-Operation (20)
    ///   Mode       : Synchronous
    ///   Filtering  : sm_esthoursrequired, sm_noofdaysrequired, sm_currentstatus
    ///   Pre Image  : name "PreImage", alias "PreImage",
    ///                columns: sm_esthoursrequired, sm_noofdaysrequired, sm_currentstatus, sm_storyname
    ///   Rank       : 1
    ///
    /// Rules (each runs only when its own column really changed, compared with the Pre Image):
    ///   Est. Hours Required changed  -> must have a value, 0.01 to 10000
    ///   No of Days Required changed  -> must have a value, whole number 1 to 365
    ///   Hours or Days changed        -> not allowed while the Story is Completed
    ///   Status changed               -> must not be cleared
    /// All problems found are reported together so the user can fix them in one go.
    /// </summary>
    public class ValidateStoryUpdate : IPlugin
    {
        private const string HoursAttr = "sm_esthoursrequired";
        private const string DaysAttr = "sm_noofdaysrequired";

        // Keep these the same as the limits in the Task 1 plug-in.
        private const decimal MinHours = 0.01m;
        private const decimal MaxHours = 10000m;
        private const int MinDays = 1;
        private const int MaxDays = 365;

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("ValidateStoryUpdate: start. Depth={0}", context.Depth);

                if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryStatusHelper.StoryEntity ||
                    !(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
                {
                    tracing.Trace("Not a Story Update. Exiting.");
                    return;
                }

                var target = (Entity)context.InputParameters["Target"];
                Entity pre = StoryStatusHelper.GetPreImage(context);

                if (pre == null)
                {
                    tracing.Trace("Pre Image 'PreImage' is missing from this step.");
                    throw new InvalidPluginExecutionException(
                        "The Story could not be validated because the plug-in step is not configured correctly " +
                        "(the 'PreImage' is missing). Please contact your administrator.");
                }

                // ---- Compare existing (Pre Image) and new (Target) values --------------------
                bool hoursChanged = target.Contains(HoursAttr) &&
                    target.GetAttributeValue<decimal?>(HoursAttr) != pre.GetAttributeValue<decimal?>(HoursAttr);

                bool daysChanged = target.Contains(DaysAttr) &&
                    target.GetAttributeValue<int?>(DaysAttr) != pre.GetAttributeValue<int?>(DaysAttr);

                OptionSetValue oldStatus = StoryStatusHelper.GetStatus(pre, StoryStatusHelper.StoryStatusAttr);
                int? oldStatusValue = oldStatus == null ? (int?)null : oldStatus.Value;
                bool statusChanged = false;
                if (target.Contains(StoryStatusHelper.StoryStatusAttr))
                {
                    OptionSetValue sent = StoryStatusHelper.GetStatus(target, StoryStatusHelper.StoryStatusAttr);
                    int? newStatusValue = sent == null ? (int?)null : sent.Value;
                    statusChanged = newStatusValue != oldStatusValue;
                }

                tracing.Trace("Changed? hours={0}, days={1}, status={2}", hoursChanged, daysChanged, statusChanged);

                if (!hoursChanged && !daysChanged && !statusChanged)
                {
                    tracing.Trace("No relevant value changed. Exiting.");
                    return;
                }

                string storyName = StoryStatusHelper.StoryNameFrom(target, pre);
                var errors = new List<string>();

                // ---- Est. Hours Required -----------------------------------------------------
                if (hoursChanged)
                {
                    decimal? oldHours = pre.GetAttributeValue<decimal?>(HoursAttr);
                    decimal? newHours = target.GetAttributeValue<decimal?>(HoursAttr);
                    tracing.Trace("Hours: {0} -> {1}", oldHours, newHours);

                    if (!newHours.HasValue)
                        errors.Add("Est. Hours Required cannot be cleared. Enter a number between " +
                            Fmt(MinHours) + " and " + Fmt(MaxHours) + ".");
                    else if (newHours.Value < MinHours)
                        errors.Add(string.Format(CultureInfo.InvariantCulture,
                            "Est. Hours Required must be greater than 0 (you entered {0}). Enter at least {1}.",
                            Fmt(newHours.Value), Fmt(MinHours)));
                    else if (newHours.Value > MaxHours)
                        errors.Add(string.Format(CultureInfo.InvariantCulture,
                            "Est. Hours Required cannot exceed {0} (you entered {1}). Reduce the value or split the work into several Stories.",
                            Fmt(MaxHours), Fmt(newHours.Value)));
                }

                // ---- No of Days Required -----------------------------------------------------
                if (daysChanged)
                {
                    int? oldDays = pre.GetAttributeValue<int?>(DaysAttr);
                    int? newDays = target.GetAttributeValue<int?>(DaysAttr);
                    tracing.Trace("Days: {0} -> {1}", oldDays, newDays);

                    if (!newDays.HasValue)
                        errors.Add("No of Days Required cannot be cleared. Enter a whole number between " +
                            MinDays + " and " + MaxDays + ".");
                    else if (newDays.Value < MinDays)
                        errors.Add(string.Format(CultureInfo.InvariantCulture,
                            "No of Days Required must be at least {0} (you entered {1}). Enter a whole number of {0} or more.",
                            MinDays, newDays.Value));
                    else if (newDays.Value > MaxDays)
                        errors.Add(string.Format(CultureInfo.InvariantCulture,
                            "No of Days Required cannot exceed {0} (you entered {1}). Reduce the days or split the work into several Stories.",
                            MaxDays, newDays.Value));
                }

                // ---- Status-related rules: run only when the status column is involved --------
                if (statusChanged)
                {
                    OptionSetValue newStatus = StoryStatusHelper.GetStatus(target, StoryStatusHelper.StoryStatusAttr);
                    tracing.Trace("Status: {0} -> {1}", oldStatus == null ? "null" : oldStatus.Value.ToString(),
                        newStatus == null ? "null" : newStatus.Value.ToString());

                    if (newStatus == null)
                        errors.Add("Current Status cannot be cleared. Select a status (for example Open or In-Progress).");
                }

                // ---- Hours / Days are locked on a Completed Story ----------------------------
                if (hoursChanged || daysChanged)
                {
                    IOrganizationService service = factory.CreateOrganizationService(null);
                    int completed = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.StoryEntity,
                        StoryStatusHelper.StoryStatusAttr, StoryStatusHelper.CompletedLabel, tracing);

                    // The status the Story will have after this save.
                    OptionSetValue effective = target.Contains(StoryStatusHelper.StoryStatusAttr)
                        ? StoryStatusHelper.GetStatus(target, StoryStatusHelper.StoryStatusAttr)
                        : oldStatus;

                    if (effective != null && effective.Value == completed)
                    {
                        var changed = new List<string>();
                        if (hoursChanged) changed.Add("Est. Hours Required");
                        if (daysChanged) changed.Add("No of Days Required");
                        errors.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0} cannot be changed while the Story is Completed. Reopen the Story first " +
                            "(set the status to Open or In-Progress), then change the value.",
                            string.Join(" and ", changed)));
                    }
                }

                // ---- Report everything at once ------------------------------------------------
                if (errors.Count == 1)
                    throw new InvalidPluginExecutionException("'" + storyName + "' was not saved. " + errors[0]);

                if (errors.Count > 1)
                {
                    var numbered = errors.Select((e, i) => (i + 1).ToString(CultureInfo.InvariantCulture) + ") " + e);
                    throw new InvalidPluginExecutionException("'" + storyName + "' was not saved. Please correct the following: " +
                        string.Join(" ", numbered));
                }

                tracing.Trace("ValidateStoryUpdate: all checks passed.");
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "The Story could not be validated. Please try again or contact your administrator. " +
                    "Reference: " + context.CorrelationId, ex);
            }
        }

        private static string Fmt(decimal value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
