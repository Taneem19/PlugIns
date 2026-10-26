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
    /// Task 6 - Delete a Story's Story Items when the Story is deleted.
    ///
    /// Registration:
    ///   Table      : Story (sm_story)
    ///   Message    : Delete
    ///   Stage      : Pre-Operation (20)
    ///   Mode       : Synchronous
    ///   Filtering  : n/a (Delete)
    ///   Images     : none
    ///
    /// Pre-Operation is used so the items are removed BEFORE the Story is deleted. If the
    /// Story to Story Item relationship is set to Restrict (or any rule that blocks deleting
    /// a parent with children), deleting the Story would otherwise be refused by the platform.
    /// If anything fails the plug-in throws, so the Story is NOT deleted and the user sees which
    /// items could not be removed. The step is safe to retry: it only deletes what is still there.
    /// </summary>
    public class DeleteStoryItemsOnStoryDelete : IPlugin
    {
        private const int BatchSize = 500;          // ExecuteMultiple hard limit is 1000
        private const int MaxFailuresListed = 5;

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("DeleteStoryItemsOnStoryDelete: start. Depth={0}", context.Depth);

                if (!string.Equals(context.MessageName, "Delete", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryStatusHelper.StoryEntity ||
                    !(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is EntityReference))
                {
                    tracing.Trace("Not a Story Delete. Exiting.");
                    return;
                }

                var storyRef = (EntityReference)context.InputParameters["Target"];
                tracing.Trace("Deleting Story {0}", storyRef.Id);

                // Read as the system so EVERY item is found, whatever the user can see.
                IOrganizationService readService = factory.CreateOrganizationService(null);
                // Delete as the calling user so security roles are respected.
                IOrganizationService deleteService = factory.CreateOrganizationService(context.UserId);

                // 1. Retrieve all related Story Items (paged QueryExpression).
                List<Entity> items = StoryStatusHelper.RetrieveItems(readService, storyRef.Id,
                    new ColumnSet(StoryStatusHelper.ItemNameAttr));

                tracing.Trace("Found {0} related Story Item(s).", items.Count);

                // 2. No items: let the Story delete continue.
                if (items.Count == 0)
                {
                    tracing.Trace("No related Story Items. Story deletion continues.");
                    return;
                }

                string storyName = GetStoryName(readService, storyRef);

                // 3. Delete them in batches with ExecuteMultipleRequest.
                var failures = new List<string>();
                int deleted = 0;

                for (int offset = 0; offset < items.Count; offset += BatchSize)
                {
                    List<Entity> chunk = items.Skip(offset).Take(BatchSize).ToList();

                    var multiple = new ExecuteMultipleRequest
                    {
                        Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
                        Requests = new OrganizationRequestCollection()
                    };
                    foreach (Entity item in chunk)
                        multiple.Requests.Add(new DeleteRequest { Target = new EntityReference(StoryStatusHelper.ItemEntity, item.Id) });

                    tracing.Trace("Deleting batch starting at {0} ({1} item(s)).", offset + 1, chunk.Count);
                    var response = (ExecuteMultipleResponse)deleteService.Execute(multiple);

                    foreach (var r in response.Responses)
                    {
                        if (r.Fault == null) { deleted++; continue; }

                        Entity failedItem = chunk[r.RequestIndex];
                        string name = failedItem.GetAttributeValue<string>(StoryStatusHelper.ItemNameAttr) ?? failedItem.Id.ToString();
                        failures.Add(name + " (" + r.Fault.Message + ")");
                        tracing.Trace("Delete failed for '{0}' ({1}): {2}", name, failedItem.Id, r.Fault.Message);
                    }
                }

                tracing.Trace("Deleted {0} of {1} Story Item(s); {2} failure(s).", deleted, items.Count, failures.Count);

                // 4. Any failure stops the Story deletion with a clear message.
                if (failures.Count > 0)
                {
                    string listed = string.Join("; ", failures.Take(MaxFailuresListed));
                    if (failures.Count > MaxFailuresListed)
                        listed += "; and " + (failures.Count - MaxFailuresListed).ToString(CultureInfo.InvariantCulture) + " more";

                    throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                        "The Story '{0}' was not deleted because {1} of its {2} Story Item(s) could not be deleted: {3}. " +
                        "Some items may already have been removed. Fix the problem (for example, ask an administrator for " +
                        "permission to delete Story Items) and delete the Story again to remove the rest.",
                        storyName, failures.Count, items.Count, listed));
                }

                tracing.Trace("DeleteStoryItemsOnStoryDelete: success. Story Items deleted: {0}.", deleted);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "The Story was not deleted because its Story Items could not be removed. " +
                    "Please try again or contact your administrator. Reference: " + context.CorrelationId, ex);
            }
        }

        private static string GetStoryName(IOrganizationService service, EntityReference storyRef)
        {
            try
            {
                Entity story = service.Retrieve(storyRef.LogicalName, storyRef.Id,
                    new ColumnSet(StoryStatusHelper.StoryNameAttr));
                return story.GetAttributeValue<string>(StoryStatusHelper.StoryNameAttr) ?? "this Story";
            }
            catch (Exception)
            {
                return "this Story";
            }
        }
    }
}
