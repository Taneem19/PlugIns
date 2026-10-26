using System;
using System.Globalization;
using System.Security;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 1 - Validate and initialize a Story during Create.
    ///
    /// Registration:
    ///   Table     : Story
    ///   Message   : Create
    ///   Stage     : Pre-Operation (20)
    ///   Mode      : Synchronous
    ///   Filtering : n/a (Create)
    ///
    /// Pre-Operation is used so that (a) a thrown exception rolls back the
    /// create and shows the message to the user, and (b) values written to the
    /// Target are saved with the record in the same transaction.
    /// </summary>
    public class ValidateAndInitializeStoryOnCreate : IPlugin
    {
        // ---- Schema names: replace "new_" with your publisher prefix. -------
        private const string StoryEntity = "sm_story";
        private const string NameAttr = "sm_storyname";
        private const string HoursAttr = "sm_esthoursrequired";      // Decimal
        private const string DaysAttr = "sm_noofdaysrequired";       // Whole Number
        private const string OpenItemsAttr = "sm_openstoryitems";    // Whole Number
        private const string InProgressAttr = "sm_inprogressstoryitems";
        private const string CompletedAttr = "sm_completedstoryitems";
        private const string ActualHoursAttr = "sm_actualhoursspent"; // Decimal

        // ---- Business limits (tune to your requirements) --------------------
        private const int NameMaxLength = 100;
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
                tracing.Trace("ValidateAndInitializeStoryOnCreate: start. Depth={0}", context.Depth);

                // Guard: correct message / entity / target type.
                if (!string.Equals(context.MessageName, "Create", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryEntity ||
                    !(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
                {
                    tracing.Trace("Not a Story Create with an Entity target. Exiting.");
                    return;
                }

                var target = (Entity)context.InputParameters["Target"];

                // Run as the calling user so security roles are respected.
                IOrganizationService service = factory.CreateOrganizationService(context.UserId);

                // 1. Read values
                string name = target.GetAttributeValue<string>(NameAttr);
                decimal? hours = target.Contains(HoursAttr) ? target.GetAttributeValue<decimal?>(HoursAttr) : null;
                int? days = target.Contains(DaysAttr) ? target.GetAttributeValue<int?>(DaysAttr) : null;
                string trimmedName = name == null ? null : name.Trim();

                tracing.Trace("Input: Name='{0}', Hours={1}, Days={2}", trimmedName, hours, days);

                // 2. Validate
                Validate(trimmedName, hours, days, tracing);

                // Persist the trimmed name so duplicates differing only by
                // spaces cannot slip through.
                target[NameAttr] = trimmedName;

                // 3. Duplicate check (FetchXML)
                if (DuplicateExists(service, trimmedName, hours.Value, days.Value, tracing))
                {
                    throw new InvalidPluginExecutionException(string.Format(
                        CultureInfo.InvariantCulture,
                        "A Story named '{0}' with {1} estimated hours and {2} day(s) required already exists. " +
                        "Change the Story Name, Est. Hours Required, or No of Days Required, or open the existing Story instead.",
                        trimmedName, hours.Value.ToString("0.##", CultureInfo.InvariantCulture), days.Value));
                }

                // 4. Initialize counters (always overwrite what the user/import sent)
                target[OpenItemsAttr] = days.Value;
                target[InProgressAttr] = 0;
                target[CompletedAttr] = 0;
                target[ActualHoursAttr] = 0m;

                tracing.Trace("Initialized: Open={0}, InProgress=0, Completed=0, ActualHours=0", days.Value);
                tracing.Trace("ValidateAndInitializeStoryOnCreate: success.");
            }
            catch (InvalidPluginExecutionException)
            {
                // Business validation message: show to the user unchanged.
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "Something went wrong while creating the Story. Please try again or contact your administrator. " +
                    "Reference: " + context.CorrelationId, ex);
            }
        }

        private static void Validate(string name, decimal? hours, int? days, ITracingService tracing)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidPluginExecutionException(
                    "Story Name is required. Enter a name that is not blank.");

            if (name.Length > NameMaxLength)
                throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                    "Story Name is too long ({0} characters). Shorten it to {1} characters or fewer.",
                    name.Length, NameMaxLength));

            if (!hours.HasValue)
                throw new InvalidPluginExecutionException(
                    "Est. Hours Required is required. Enter the estimated hours as a number greater than 0.");

            if (hours.Value < MinHours)
                throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                    "Est. Hours Required must be greater than 0 (you entered {0}). Enter at least {1}.",
                    hours.Value.ToString("0.##", CultureInfo.InvariantCulture), MinHours));

            if (hours.Value > MaxHours)
                throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                    "Est. Hours Required cannot exceed {0} (you entered {1}). Reduce the value or split the work into multiple Stories.",
                    MaxHours, hours.Value.ToString("0.##", CultureInfo.InvariantCulture)));

            if (!days.HasValue)
                throw new InvalidPluginExecutionException(
                    "No of Days Required is required. Enter a whole number of days of at least 1.");

            if (days.Value < MinDays)
                throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                    "No of Days Required must be at least {0} (you entered {1}). Enter a whole number of 1 or more.",
                    MinDays, days.Value));

            if (days.Value > MaxDays)
                throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                    "No of Days Required cannot exceed {0} (you entered {1}). Reduce the days or split the work into multiple Stories.",
                    MaxDays, days.Value));

            tracing.Trace("Validation passed.");
        }

        private static bool DuplicateExists(IOrganizationService service, string name, decimal hours, int days, ITracingService tracing)
        {
            // SecurityElement.Escape protects against XML-special characters in the name.
            string fetchXml = string.Format(CultureInfo.InvariantCulture,
                @"<fetch top='1' no-lock='true'>
                    <entity name='{0}'>
                      <attribute name='{0}id' />
                      <filter type='and'>
                        <condition attribute='{1}' operator='eq' value='{2}' />
                        <condition attribute='{3}' operator='eq' value='{4}' />
                        <condition attribute='{5}' operator='eq' value='{6}' />
                        <condition attribute='statecode' operator='eq' value='0' />
                      </filter>
                    </entity>
                  </fetch>",
                StoryEntity,
                NameAttr, SecurityElement.Escape(name),
                HoursAttr, hours.ToString(CultureInfo.InvariantCulture),
                DaysAttr, days.ToString(CultureInfo.InvariantCulture));

            tracing.Trace("Duplicate FetchXML: {0}", fetchXml);

            EntityCollection result = service.RetrieveMultiple(new FetchExpression(fetchXml));
            tracing.Trace("Duplicate check returned {0} record(s).", result.Entities.Count);
            return result.Entities.Count > 0;
        }
    }
}
