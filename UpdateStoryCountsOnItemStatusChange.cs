using System;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 4 - When a Story Item's status changes, refresh the parent Story's summary counts.
    ///
    /// Registration:
    ///   Table      : Story Item (sm_storyitem)
    ///   Message    : Update
    ///   Stage      : Post-Operation (40)  (the new status is already in the transaction,
    ///                                      so the recount includes the change)
    ///   Mode       : Synchronous
    ///   Filtering  : Story Item status column only (sm_currentstatus)
    ///   Images     : none (the counts are recalculated from all items, so no previous value is needed)
    ///
    /// The counts are recalculated from scratch every time, so a wrong value is
    /// corrected by the next status change. Updating the Story's count columns does not
    /// trigger the Task 3 Story steps, because those filter on the Story status only.
    /// </summary>
    public class UpdateStoryCountsOnItemStatusChange : IPlugin
    {
        // Story summary columns
        private const string OpenCountAttr = "sm_openstoryitems";
        private const string InProgressCountAttr = "sm_inprogressstoryitems";
        private const string CompletedCountAttr = "sm_completedstoryitems";

        private const int MaxDepth = 4;

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("UpdateStoryCountsOnItemStatusChange: start. Depth={0}", context.Depth);

                if (context.Depth > MaxDepth)
                {
                    tracing.Trace("Depth {0} exceeds {1}. Exiting to avoid a loop.", context.Depth, MaxDepth);
                    return;
                }

                if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryStatusHelper.ItemEntity ||
                    !(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
                {
                    tracing.Trace("Not a Story Item Update. Exiting.");
                    return;
                }

                var target = (Entity)context.InputParameters["Target"];
                if (!target.Contains(StoryStatusHelper.ItemStatusAttr))
                {
                    tracing.Trace("Status not in this update. Exiting.");
                    return;
                }

                // System context: the recount must see every item of the Story, and the
                // Story update must not depend on the item editor's rights on the Story.
                IOrganizationService service = factory.CreateOrganizationService(null);

                // 1. Find the parent Story.
                Entity item = service.Retrieve(StoryStatusHelper.ItemEntity, context.PrimaryEntityId,
                    new ColumnSet(StoryStatusHelper.ItemStoryLookupAttr));
                EntityReference parent = item.GetAttributeValue<EntityReference>(StoryStatusHelper.ItemStoryLookupAttr);

                if (parent == null)
                {
                    tracing.Trace("Story Item {0} has no parent Story. Nothing to update.", context.PrimaryEntityId);
                    return;
                }

                // 2. Resolve status values from metadata (never hard-coded).
                int open = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.ItemEntity,
                    StoryStatusHelper.ItemStatusAttr, StoryStatusHelper.OpenLabel, tracing);
                int inProgress = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.ItemEntity,
                    StoryStatusHelper.ItemStatusAttr, StoryStatusHelper.InProgressLabel, tracing);
                int completed = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.ItemEntity,
                    StoryStatusHelper.ItemStatusAttr, StoryStatusHelper.CompletedLabel, tracing);

                // 3. Retrieve all items of the Story and count by status.
                var items = StoryStatusHelper.RetrieveItems(service, parent.Id,
                    new ColumnSet(StoryStatusHelper.ItemStatusAttr));

                int openCount = 0, progressCount = 0, completedCount = 0;
                foreach (Entity i in items)
                {
                    OptionSetValue s = i.GetAttributeValue<OptionSetValue>(StoryStatusHelper.ItemStatusAttr);
                    if (s == null) continue;
                    if (s.Value == open) openCount++;
                    else if (s.Value == inProgress) progressCount++;
                    else if (s.Value == completed) completedCount++;
                }

                tracing.Trace("Story {0}: {1} item(s) -> Open={2}, In-Progress={3}, Completed={4}",
                    parent.Id, items.Count, openCount, progressCount, completedCount);

                // 4. Update the Story only if something changed (avoids needless saves and audit noise).
                Entity story = service.Retrieve(parent.LogicalName, parent.Id,
                    new ColumnSet(OpenCountAttr, InProgressCountAttr, CompletedCountAttr));

                if (story.GetAttributeValue<int?>(OpenCountAttr) == openCount &&
                    story.GetAttributeValue<int?>(InProgressCountAttr) == progressCount &&
                    story.GetAttributeValue<int?>(CompletedCountAttr) == completedCount)
                {
                    tracing.Trace("Counts already correct. No update needed.");
                    return;
                }

                var update = new Entity(parent.LogicalName, parent.Id);
                update[OpenCountAttr] = openCount;
                update[InProgressCountAttr] = progressCount;
                update[CompletedCountAttr] = completedCount;
                service.Update(update);

                tracing.Trace("UpdateStoryCountsOnItemStatusChange: Story counts updated.");
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "The Story Item status could not be saved because the parent Story's item counts could not be refreshed. " +
                    "Please try again or contact your administrator. Reference: " + context.CorrelationId, ex);
            }
        }
    }
}
