using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 3b - When a Completed Story is reopened, set all its Story Items to the same status.
    ///
    /// Registration:
    ///   Table      : Story (sm_story)
    ///   Message    : Update
    ///   Stage      : Post-Operation (40)
    ///   Mode       : ASYNCHRONOUS (ExecuteMultipleRequest does not run inside the caller's
    ///                              transaction, so it runs after the Story change is committed)
    ///   Filtering  : Story status column only (sm_status)
    ///   Pre Image  : name "PreImage", columns: sm_status, sm_storyname
    /// </summary>
    public class ReopenStoryItems : IPlugin
    {
        private const int BatchSize = 500;

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("ReopenStoryItems: start. Depth={0}", context.Depth);

                if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryStatusHelper.StoryEntity ||
                    !(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
                {
                    return;
                }

                var target = (Entity)context.InputParameters["Target"];
                OptionSetValue newStatus = StoryStatusHelper.GetStatus(target, StoryStatusHelper.StoryStatusAttr);
                if (newStatus == null) return;

                Entity pre = StoryStatusHelper.GetPreImage(context);
                OptionSetValue oldStatus = StoryStatusHelper.GetStatus(pre, StoryStatusHelper.StoryStatusAttr);
                if (oldStatus == null)
                {
                    tracing.Trace("No Pre Image status. Check the step's Pre Image. Exiting.");
                    return;
                }

                IOrganizationService service = factory.CreateOrganizationService(null);

                int completed = StoryStatusHelper.ResolveOptionValue(service, StoryStatusHelper.StoryEntity,
                    StoryStatusHelper.StoryStatusAttr, StoryStatusHelper.CompletedLabel, tracing);

                // Reopen = was Completed, now something else.
                if (oldStatus.Value != completed || newStatus.Value == completed)
                {
                    tracing.Trace("Not a reopen (old={0}, new={1}). Exiting.", oldStatus.Value, newStatus.Value);
                    return;
                }

                int itemStatus = StoryStatusHelper.MapOption(service, StoryStatusHelper.StoryEntity,
                    StoryStatusHelper.StoryStatusAttr, newStatus.Value, StoryStatusHelper.ItemEntity,
                    StoryStatusHelper.ItemStatusAttr, tracing);

                var items = StoryStatusHelper.RetrieveItems(service, context.PrimaryEntityId,
                    new ColumnSet(StoryStatusHelper.ItemNameAttr, StoryStatusHelper.ItemStatusAttr));

                var toUpdate = items.Where(i =>
                {
                    var s = i.GetAttributeValue<OptionSetValue>(StoryStatusHelper.ItemStatusAttr);
                    return s == null || s.Value != itemStatus;
                }).ToList();

                tracing.Trace("Story Items: {0} total, {1} to update to status {2}.", items.Count, toUpdate.Count, itemStatus);
                if (toUpdate.Count == 0) return;

                var requests = toUpdate.Select(i =>
                {
                    var update = new Entity(StoryStatusHelper.ItemEntity, i.Id);
                    update[StoryStatusHelper.ItemStatusAttr] = new OptionSetValue(itemStatus);
                    return (OrganizationRequest)new UpdateRequest { Target = update };
                }).ToList();

                var failures = new List<string>();
                for (int offset = 0; offset < requests.Count; offset += BatchSize)
                {
                    var chunk = requests.Skip(offset).Take(BatchSize).ToList();
                    var multiple = new ExecuteMultipleRequest
                    {
                        Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
                        Requests = new OrganizationRequestCollection()
                    };
                    multiple.Requests.AddRange(chunk);

                    tracing.Trace("Executing batch starting at {0} ({1} update(s)).", offset + 1, chunk.Count);
                    var response = (ExecuteMultipleResponse)service.Execute(multiple);

                    foreach (var r in response.Responses.Where(r => r.Fault != null))
                    {
                        var failedItem = toUpdate[offset + r.RequestIndex];
                        string name = failedItem.GetAttributeValue<string>(StoryStatusHelper.ItemNameAttr) ?? failedItem.Id.ToString();
                        failures.Add(name + " (" + r.Fault.Message + ")");
                        tracing.Trace("Update failed for '{0}': {1}", name, r.Fault.Message);
                    }
                }

                if (failures.Count > 0)
                {
                    throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                        "The Story was reopened, but {0} of {1} Story Item(s) could not be updated to the same status: {2}. " +
                        "Update these items manually or fix the problem and reopen the Story again.",
                        failures.Count, toUpdate.Count, string.Join("; ", failures.Take(5)) +
                        (failures.Count > 5 ? "; and " + (failures.Count - 5) + " more" : string.Empty)));
                }

                tracing.Trace("ReopenStoryItems: success. Updated {0} Story Item(s).", toUpdate.Count);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "The Story was reopened, but its Story Items could not be updated. " +
                    "Please contact your administrator. Reference: " + context.CorrelationId, ex);
            }
        }
    }
}
