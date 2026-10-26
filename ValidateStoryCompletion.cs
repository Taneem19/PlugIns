using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 3a - Block completing a Story while any Story Item is Open or In-Progress.
    ///
    /// Registration:
    ///   Table      : Story (sm_story)
    ///   Message    : Update
    ///   Stage      : Pre-Operation (20)
    ///   Mode       : Synchronous
    ///   Filtering  : Story status column only (sm_status)
    ///   Pre Image  : name "PreImage", columns: sm_status, sm_storyname
    ///   Rank       : 1 (runs before the other Task 3 pre-operation step)
    /// </summary>
    public class ValidateStoryCompletion : IPlugin
    {
        private const int MaxItemsListed = 10;

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("ValidateStoryCompletion: start. Depth={0}", context.Depth);

                if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryStatusHelper.StoryEntity ||
                    !(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
                {
                    tracing.Trace("Not a Story Update. Exiting.");
                    return;
                }

                var target = (Entity)context.InputParameters["Target"];
                if (!target.Contains(StoryStatusHelper.StoryStatusAttr))
                {
                    tracing.Trace("Status not in this update. Exiting.");
                    return;
                }

                // System context: the check must see every item, whatever the user's read scope.
                IOrganizationService service = factory.CreateOrganizationService(null);

                int completed = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.StoryEntity,
                    StoryStatusHelper.StoryStatusAttr, StoryStatusHelper.CompletedLabel, tracing);

                if (!StoryStatusHelper.IsMovingTo(context, target, completed))
                {
                    tracing.Trace("Not a change to Completed. Exiting.");
                    return;
                }

                int open = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.ItemEntity,
                    StoryStatusHelper.ItemStatusAttr, StoryStatusHelper.OpenLabel, tracing);
                int inProgress = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.ItemEntity,
                    StoryStatusHelper.ItemStatusAttr, StoryStatusHelper.InProgressLabel, tracing);

                List<Entity> unfinished = StoryStatusHelper.RetrieveItems(service, context.PrimaryEntityId,
                    new ColumnSet(StoryStatusHelper.ItemNameAttr, StoryStatusHelper.ItemStatusAttr), open, inProgress);

                tracing.Trace("Unfinished Story Items: {0}", unfinished.Count);

                if (unfinished.Count > 0)
                {
                    int openCount = unfinished.Count(i => i.GetAttributeValue<OptionSetValue>(StoryStatusHelper.ItemStatusAttr).Value == open);
                    int progressCount = unfinished.Count - openCount;

                    var names = unfinished
                        .Select(i => i.GetAttributeValue<string>(StoryStatusHelper.ItemNameAttr))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .Take(MaxItemsListed).ToList();

                    var sb = new StringBuilder();
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "'{0}' cannot be marked as Completed because {1} Story Item(s) are not finished ({2} Open, {3} In-Progress). ",
                        StoryStatusHelper.StoryNameFrom(target, StoryStatusHelper.GetPreImage(context)),
                        unfinished.Count, openCount, progressCount);
                    if (names.Count > 0)
                    {
                        sb.Append("Complete these items first: ").Append(string.Join(", ", names));
                        if (unfinished.Count > names.Count) sb.AppendFormat(CultureInfo.InvariantCulture, " and {0} more", unfinished.Count - names.Count);
                        sb.Append(". ");
                    }
                    sb.Append("Then set the Story to Completed again.");

                    throw new InvalidPluginExecutionException(sb.ToString());
                }

                tracing.Trace("ValidateStoryCompletion: all Story Items are finished. Completion allowed.");
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "The Story status could not be validated. Please try again or contact your administrator. " +
                    "Reference: " + context.CorrelationId, ex);
            }
        }
    }
}
