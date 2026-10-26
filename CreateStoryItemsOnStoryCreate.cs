using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 2 - Create Story Items in a batch when a Story is created.
    ///
    /// Registration:
    ///   Table     : Story
    ///   Message   : Create
    ///   Stage     : Post-Operation (40)
    ///   Mode      : ASYNCHRONOUS (ExecuteMultiple cannot see an uncommitted Story,
    ///               so the batch must run after the Story is saved)
    ///   Filtering : n/a (Create)
    ///
    /// Post-Operation is used because the Story row must exist before the
    /// Story Item lookup can point to it. Running synchronously means the Story
    /// and all Story Items share one transaction: if anything fails, the plugin
    /// throws and Dataverse rolls back the Story AND any items already created,
    /// so no half-built Story is ever left behind.
    /// </summary>
    public class CreateStoryItemsOnStoryCreate : IPlugin
    {
        // ---- Schema names: replace "sm_" with your publisher prefix. -------
        private const string StoryEntity = "sm_story";
        private const string StoryNameAttr = "sm_storyname";
        private const string StoryHoursAttr = "sm_esthoursrequired";
        private const string StoryDaysAttr = "sm_noofdaysrequired";
        private const string OwnerAttr = "ownerid";

        private const string ItemEntity = "sm_storyitem";
        private const string ItemNameAttr = "sm_storyitemname";
        private const string ItemStoryLookupAttr = "sm_story";
        private const string ItemHoursAttr = "sm_hoursrequired";
        private const string ItemStatusAttr = "sm_currentstatus";        // Choice column

        // Label of the "Open" option. The numeric value is looked up from
        // metadata at runtime, never hard-coded.
        private const string OpenStatusLabel = "Open";

        // ---- Limits / settings ----------------------------------------------
        private const int HoursDecimalPlaces = 2;      // must match the column precision
        private const int ItemNameMaxLength = 100;     // must match the column length
        private const int BatchSize = 500;             // ExecuteMultiple hard limit is 1000

        // If item creation fails, partially created items are always removed.
        // true  = the Story itself is also deleted (nothing half-built remains)
        // false = the Story is kept (with no items) and the error is shown in System Jobs
        private const bool DeleteStoryOnFailure = false;

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("CreateStoryItemsOnStoryCreate: start. Depth={0}", context.Depth);

                if (!string.Equals(context.MessageName, "Create", StringComparison.OrdinalIgnoreCase) ||
                    context.PrimaryEntityName != StoryEntity ||
                    context.PrimaryEntityId == Guid.Empty)
                {
                    tracing.Trace("Not a Story Create. Exiting.");
                    return;
                }

                // PrimaryEntityId is reliable in asynchronous steps (OutputParameters may be empty).
                Guid storyId = context.PrimaryEntityId;
                IOrganizationService service = factory.CreateOrganizationService(context.UserId);

                // 1. Read the saved Story (values include those set by Task 1).
                Entity story = service.Retrieve(StoryEntity, storyId,
                    new ColumnSet(StoryNameAttr, StoryHoursAttr, StoryDaysAttr, OwnerAttr));

                string storyName = story.GetAttributeValue<string>(StoryNameAttr) ?? string.Empty;
                decimal totalHours = story.GetAttributeValue<decimal?>(StoryHoursAttr) ?? 0m;
                int days = story.GetAttributeValue<int?>(StoryDaysAttr) ?? 0;
                EntityReference owner = story.GetAttributeValue<EntityReference>(OwnerAttr);

                tracing.Trace("Story {0}: Name='{1}', Hours={2}, Days={3}, Owner={4}",
                    storyId, storyName, totalHours, days, owner == null ? "null" : owner.Id.ToString());

                if (days <= 0)
                {
                    // Task 1 normally blocks this. Nothing to create.
                    tracing.Trace("No of Days Required <= 0. No Story Items to create.");
                    return;
                }

                if (owner == null)
                    throw new InvalidPluginExecutionException(
                        "The Story has no owner, so its Story Items cannot be assigned. Set an owner on the Story and save again.");

                // 2. Resolve the "Open" status value from metadata.
                int openStatusValue = ResolveOptionValue(service, ItemEntity, ItemStatusAttr, OpenStatusLabel, tracing);

                // 3. Build requests (equal hours split; remainder goes to the last item).
                decimal[] hoursPerItem = SplitHours(totalHours, days, HoursDecimalPlaces);
                var requests = new List<OrganizationRequest>(days);

                for (int i = 1; i <= days; i++)
                {
                    var item = new Entity(ItemEntity);
                    item[ItemNameAttr] = BuildItemName(storyName, i);
                    item[ItemStoryLookupAttr] = new EntityReference(StoryEntity, storyId);
                    item[ItemHoursAttr] = hoursPerItem[i - 1];
                    item[ItemStatusAttr] = new OptionSetValue(openStatusValue);
                    item[OwnerAttr] = new EntityReference(owner.LogicalName, owner.Id);

                    requests.Add(new CreateRequest { Target = item });
                }

                tracing.Trace("Prepared {0} Story Item create request(s). Hours per item (first/last)={1}/{2}",
                    requests.Count, hoursPerItem[0], hoursPerItem[days - 1]);

                // 4. Execute in batches with ExecuteMultipleRequest.
                var createdIds = new List<Guid>();
                try
                {
                    ExecuteInBatches(service, requests, storyName, createdIds, tracing);
                }
                catch (Exception)
                {
                    // Compensate: remove any items that were created before the failure.
                    Cleanup(service, storyId, createdIds, tracing);
                    throw;
                }

                tracing.Trace("CreateStoryItemsOnStoryCreate: success. Created {0} Story Item(s).", days);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "The Story Items for this Story could not be generated. " +
                    "Please contact your administrator. Reference: " + context.CorrelationId, ex);
            }
        }

        // --------------------------------------------------------------------
        private static void Cleanup(IOrganizationService service, Guid storyId, List<Guid> createdIds,
            ITracingService tracing)
        {
            foreach (Guid id in createdIds)
            {
                try { service.Delete(ItemEntity, id); }
                catch (Exception ex) { tracing.Trace("Cleanup: could not delete Story Item {0}: {1}", id, ex.Message); }
            }
            tracing.Trace("Cleanup: removed {0} partially created Story Item(s).", createdIds.Count);

            if (DeleteStoryOnFailure)
            {
                try { service.Delete(StoryEntity, storyId); tracing.Trace("Cleanup: deleted Story {0}.", storyId); }
                catch (Exception ex) { tracing.Trace("Cleanup: could not delete Story {0}: {1}", storyId, ex.Message); }
            }
        }

        private static void ExecuteInBatches(IOrganizationService service, List<OrganizationRequest> requests,
            string storyName, List<Guid> createdIds, ITracingService tracing)
        {
            for (int offset = 0; offset < requests.Count; offset += BatchSize)
            {
                var chunk = requests.Skip(offset).Take(BatchSize).ToList();

                var multiple = new ExecuteMultipleRequest
                {
                    Settings = new ExecuteMultipleSettings
                    {
                        ContinueOnError = false,   // stop at the first failure
                        ReturnResponses = true
                    },
                    Requests = new OrganizationRequestCollection()
                };
                multiple.Requests.AddRange(chunk);

                tracing.Trace("Executing batch starting at item {0} ({1} request(s)).", offset + 1, chunk.Count);

                var response = (ExecuteMultipleResponse)service.Execute(multiple);

                foreach (var r in response.Responses)
                {
                    var created = r.Response as CreateResponse;
                    if (r.Fault == null && created != null)
                        createdIds.Add(created.id);
                }

                if (response.IsFaulted)
                {
                    var failed = response.Responses.FirstOrDefault(r => r.Fault != null);
                    int itemNumber = failed == null ? offset + 1 : offset + failed.RequestIndex + 1;
                    string reason = failed == null || failed.Fault == null ? "Unknown error" : failed.Fault.Message;

                    tracing.Trace("Batch fault at Story Item {0}: {1}", itemNumber, reason);

                    // Shown in System Jobs (asynchronous); the caller then removes partial items.
                    throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                        "Story Items could not be generated for Story '{0}': Story Item {1} of {2} failed: {3} " +
                        "Any partially created items were removed. Correct the problem and create the Story again" +
                        (DeleteStoryOnFailure ? " (this Story was deleted)." : " (delete the empty Story first)."),
                        storyName, itemNumber, requests.Count, reason));
                }
            }
        }

        /// <summary>
        /// Splits total hours equally. Each item gets the rounded share; the last
        /// item absorbs the rounding remainder so the sum always equals the total.
        /// </summary>
        private static decimal[] SplitHours(decimal total, int count, int places)
        {
            var result = new decimal[count];
            decimal share = Math.Round(total / count, places, MidpointRounding.AwayFromZero);
            decimal assigned = 0m;

            for (int i = 0; i < count - 1; i++)
            {
                result[i] = share;
                assigned += share;
            }
            result[count - 1] = total - assigned;
            return result;
        }

        private static string BuildItemName(string storyName, int dayNumber)
        {
            string suffix = " - Day " + dayNumber.ToString(CultureInfo.InvariantCulture);
            int room = ItemNameMaxLength - suffix.Length;
            string baseName = storyName.Length > room ? storyName.Substring(0, Math.Max(room, 0)) : storyName;
            return baseName + suffix;
        }

        /// <summary>Looks up the numeric value of an option by its label (any language).</summary>
        private static int ResolveOptionValue(IOrganizationService service, string entity, string attribute,
            string label, ITracingService tracing)
        {
            var response = (RetrieveAttributeResponse)service.Execute(new RetrieveAttributeRequest
            {
                EntityLogicalName = entity,
                LogicalName = attribute,
                RetrieveAsIfPublished = true
            });

            OptionMetadataCollection options = null;
            var picklist = response.AttributeMetadata as PicklistAttributeMetadata;
            var status = response.AttributeMetadata as StatusAttributeMetadata;
            if (picklist != null) options = picklist.OptionSet.Options;
            else if (status != null) options = status.OptionSet.Options;

            if (options != null)
            {
                foreach (OptionMetadata opt in options)
                {
                    if (opt.Label != null && opt.Label.LocalizedLabels.Any(l =>
                        string.Equals(l.Label, label, StringComparison.OrdinalIgnoreCase)) && opt.Value.HasValue)
                    {
                        tracing.Trace("Resolved '{0}' = {1}", label, opt.Value.Value);
                        return opt.Value.Value;
                    }
                }
            }

            throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                "The Story Item Status column has no option named '{0}'. Ask an administrator to add it " +
                "(or update the plug-in setting) before creating Stories.", label));
        }
    }
}
