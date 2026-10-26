using System;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 3c - When a Story is completed, set Actual Hours Spent = sum of its Story Items' hours.
    ///
    /// Registration:
    ///   Table      : Story (sm_story)
    ///   Message    : Update
    ///   Stage      : Pre-Operation (20)   (the value is written to the Target, so the Story
    ///                                       is updated in the same save with no extra Update call)
    ///   Mode       : Synchronous
    ///   Filtering  : Story status column only (sm_status)
    ///   Pre Image  : name "PreImage", columns: sm_status, sm_storyname
    ///   Rank       : 2 (after ValidateStoryCompletion)
    /// </summary>
    public class CalculateActualHoursOnCompletion : IPlugin
    {
        private const int HoursDecimalPlaces = 2;

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("CalculateActualHoursOnCompletion: start. Depth={0}", context.Depth);

                if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryStatusHelper.StoryEntity ||
                    !(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
                {
                    return;
                }

                var target = (Entity)context.InputParameters["Target"];
                if (!target.Contains(StoryStatusHelper.StoryStatusAttr)) return;

                IOrganizationService service = factory.CreateOrganizationService(null);

                int completed = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.StoryEntity,
                    StoryStatusHelper.StoryStatusAttr, StoryStatusHelper.CompletedLabel, tracing);

                if (!StoryStatusHelper.IsMovingTo(context, target, completed))
                {
                    tracing.Trace("Not a change to Completed. Exiting.");
                    return;
                }

                var items = StoryStatusHelper.RetrieveItems(service, context.PrimaryEntityId,
                    new ColumnSet(StoryStatusHelper.ItemActualHoursAttr));

                decimal total = items.Sum(i => i.GetAttributeValue<decimal?>(StoryStatusHelper.ItemActualHoursAttr) ?? 0m);
                total = Math.Round(total, HoursDecimalPlaces, MidpointRounding.AwayFromZero);

                target[StoryStatusHelper.StoryActualHoursAttr] = total;

                tracing.Trace("Actual Hours Spent = {0} from {1} Story Item(s).", total, items.Count);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "Actual Hours Spent could not be calculated, so the Story was not completed. " +
                    "Please try again or contact your administrator. Reference: " + context.CorrelationId, ex);
            }
        }
    }
}
