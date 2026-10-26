using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Shared helpers for the Story status plugins (Task 3).
    /// Choice values are always resolved from metadata by label, never hard-coded.
    /// </summary>
    public static class StoryStatusHelper
    {
        // ---- Schema names: change here if your logical names differ. --------
        public const string StoryEntity = "sm_story";
        public const string StoryNameAttr = "sm_storyname";
        public const string StoryStatusAttr = "sm_currentstatus";            // use "statuscode" if you use Status Reason
        public const string StoryActualHoursAttr = "sm_actualhoursspent";

        public const string ItemEntity = "sm_storyitem";
        public const string ItemNameAttr = "sm_storyitemname";
        public const string ItemStoryLookupAttr = "sm_story";
        public const string ItemStatusAttr = "sm_currentstatus";
        public const string ItemActualHoursAttr = "sm_hoursrequired";  // Story Item has no separate actual-hours column

        // ---- Option labels (case, spaces and hyphens are ignored when matching) ----
        public const string CompletedLabel = "Completed";
        public const string OpenLabel = "Open";
        public const string InProgressLabel = "In-Progress";

        public const string PreImageName = "PreImage";

        public static string Normalize(string s)
        {
            if (s == null) return string.Empty;
            return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static OptionMetadataCollection GetOptions(IOrganizationService service, string entity, string attribute)
        {
            var response = (RetrieveAttributeResponse)service.Execute(new RetrieveAttributeRequest
            {
                EntityLogicalName = entity,
                LogicalName = attribute,
                RetrieveAsIfPublished = true
            });

            var picklist = response.AttributeMetadata as PicklistAttributeMetadata;
            if (picklist != null) return picklist.OptionSet.Options;
            var status = response.AttributeMetadata as StatusAttributeMetadata;
            if (status != null) return status.OptionSet.Options;
            var state = response.AttributeMetadata as StateAttributeMetadata;
            if (state != null) return state.OptionSet.Options;
            return null;
        }

        private static IEnumerable<string> Labels(OptionMetadata option)
        {
            if (option.Label == null) yield break;
            foreach (var l in option.Label.LocalizedLabels) yield return l.Label;
        }

        /// <summary>Numeric value of the option with this label.</summary>
        public static int ResolveOptionValue(IOrganizationService service, string entity, string attribute,
            string label, ITracingService tracing)
        {
            var options = GetOptions(service, entity, attribute);
            string wanted = Normalize(label);
            if (options != null)
            {
                foreach (var opt in options)
                {
                    if (opt.Value.HasValue && Labels(opt).Any(l => Normalize(l) == wanted))
                    {
                        tracing.Trace("Resolved {0}.{1} '{2}' = {3}", entity, attribute, label, opt.Value.Value);
                        return opt.Value.Value;
                    }
                }
            }

            throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                "The '{0}' column on '{1}' has no option named '{2}'. Ask an administrator to add it " +
                "before changing Story status.", attribute, entity, label));
        }

        /// <summary>Finds the option on the destination column that has the same label as the source option.</summary>
        public static int MapOption(IOrganizationService service, string srcEntity, string srcAttribute,
            int srcValue, string dstEntity, string dstAttribute, ITracingService tracing)
        {
            var src = GetOptions(service, srcEntity, srcAttribute);
            var dst = GetOptions(service, dstEntity, dstAttribute);
            var srcOption = src == null ? null : src.FirstOrDefault(o => o.Value == srcValue);
            string srcLabel = srcOption == null ? srcValue.ToString(CultureInfo.InvariantCulture)
                : Labels(srcOption).FirstOrDefault() ?? srcValue.ToString(CultureInfo.InvariantCulture);

            if (srcOption != null && dst != null)
            {
                var wanted = Labels(srcOption).Select(Normalize).ToList();
                foreach (var opt in dst)
                {
                    if (opt.Value.HasValue && Labels(opt).Any(l => wanted.Contains(Normalize(l))))
                    {
                        tracing.Trace("Mapped Story status '{0}' ({1}) to Story Item status {2}", srcLabel, srcValue, opt.Value.Value);
                        return opt.Value.Value;
                    }
                }
            }

            throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                "The Story was reopened with status '{0}', but Story Items have no status with the same name, " +
                "so their status could not be updated. Add a '{0}' option to the Story Item status column " +
                "or reopen the Story with Open or In-Progress.", srcLabel));
        }

        public static OptionSetValue GetStatus(Entity entity, string attribute)
        {
            return entity != null && entity.Contains(attribute) ? entity.GetAttributeValue<OptionSetValue>(attribute) : null;
        }

        public static Entity GetPreImage(IPluginExecutionContext context)
        {
            return context.PreEntityImages != null && context.PreEntityImages.Contains(PreImageName)
                ? context.PreEntityImages[PreImageName] : null;
        }

        /// <summary>True when this Update moves the Story INTO the given status (it was not in it before).</summary>
        public static bool IsMovingTo(IPluginExecutionContext context, Entity target, int statusValue)
        {
            var now = GetStatus(target, StoryStatusAttr);
            if (now == null || now.Value != statusValue) return false;
            var before = GetStatus(GetPreImage(context), StoryStatusAttr);
            return before == null || before.Value != statusValue;
        }

        /// <summary>All Story Items of a Story (optionally limited to some status values), with paging.</summary>
        public static List<Entity> RetrieveItems(IOrganizationService service, Guid storyId, ColumnSet columns,
            params int[] statusValues)
        {
            var query = new QueryExpression(ItemEntity) { ColumnSet = columns };
            query.Criteria.AddCondition(ItemStoryLookupAttr, ConditionOperator.Equal, storyId);
            if (statusValues != null && statusValues.Length > 0)
                query.Criteria.AddCondition(ItemStatusAttr, ConditionOperator.In, statusValues.Cast<object>().ToArray());
            query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 };

            var all = new List<Entity>();
            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(query);
                all.AddRange(page.Entities);
                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
            return all;
        }

        public static string StoryNameFrom(Entity target, Entity preImage)
        {
            string name = target != null && target.Contains(StoryNameAttr) ? target.GetAttributeValue<string>(StoryNameAttr) : null;
            if (string.IsNullOrWhiteSpace(name) && preImage != null) name = preImage.GetAttributeValue<string>(StoryNameAttr);
            return string.IsNullOrWhiteSpace(name) ? "this Story" : name;
        }
    }
}
