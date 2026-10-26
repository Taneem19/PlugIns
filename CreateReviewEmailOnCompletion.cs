using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace StoryManagement.Plugins
{
    /// <summary>
    /// Task 3d - When a Story is completed, create a review email (draft) for the assigned user's manager.
    ///
    /// Registration:
    ///   Table      : Story (sm_story)
    ///   Message    : Update
    ///   Stage      : Post-Operation (40)
    ///   Mode       : Synchronous  (a failure rolls the completion back with a clear message)
    ///   Filtering  : Story status column only (sm_status)
    ///   Pre Image  : name "PreImage", columns: sm_status, sm_storyname
    ///
    /// To : manager (parent user) of the Story owner
    /// From: Story owner
    /// The email is created as a draft and is not sent.
    /// </summary>
    public class CreateReviewEmailOnCompletion : IPlugin
    {
        private const string SubjectSuffix = " is completed, please review";
        private const string Body =
            "The Story has been successfully marked as completed. Please review the details and ensure " +
            "everything is in order. If there are any discrepancies or further actions required, kindly " +
            "address them at your earliest convenience.";

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            try
            {
                tracing.Trace("CreateReviewEmailOnCompletion: start. Depth={0}", context.Depth);

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

                Guid storyId = context.PrimaryEntityId;

                // Current owner (the owner may have changed in this same save).
                Entity story = service.Retrieve(StoryStatusHelper.StoryEntity, storyId,
                    new ColumnSet(StoryStatusHelper.StoryNameAttr, "ownerid"));
                string storyName = story.GetAttributeValue<string>(StoryStatusHelper.StoryNameAttr) ?? "Story";
                EntityReference owner = story.GetAttributeValue<EntityReference>("ownerid");

                if (owner == null || owner.LogicalName != "systemuser")
                {
                    throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                        "'{0}' cannot be completed because it is not assigned to a user (it is owned by a team or nobody). " +
                        "Assign the Story to a user so the review email can be addressed, then complete it again.", storyName));
                }

                Entity user = service.Retrieve("systemuser", owner.Id, new ColumnSet("fullname", "parentsystemuserid"));
                string userName = user.GetAttributeValue<string>("fullname") ?? "the assigned user";
                EntityReference manager = user.GetAttributeValue<EntityReference>("parentsystemuserid");

                if (manager == null)
                {
                    throw new InvalidPluginExecutionException(string.Format(CultureInfo.InvariantCulture,
                        "'{0}' cannot be completed because {1} has no manager set, so the review email has no recipient. " +
                        "Ask an administrator to set a manager on {1}'s user record, then complete the Story again.",
                        storyName, userName));
                }

                tracing.Trace("Owner={0}, Manager={1}", owner.Id, manager.Id);

                var email = new Entity("email");
                email["subject"] = storyName + SubjectSuffix;
                email["description"] = Body;
                email["from"] = new EntityCollection(new List<Entity> { Party(owner) });
                email["to"] = new EntityCollection(new List<Entity> { Party(manager) });
                email["regardingobjectid"] = new EntityReference(StoryStatusHelper.StoryEntity, storyId);
                email["directioncode"] = true;   // outgoing

                Guid emailId = service.Create(email);
                tracing.Trace("Review email created: {0}", emailId);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("Unexpected error: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "The review email could not be created, so the Story was not completed. " +
                    "Please try again or contact your administrator. Reference: " + context.CorrelationId, ex);
            }
        }

        private static Entity Party(EntityReference user)
        {
            var party = new Entity("activityparty");
            party["partyid"] = new EntityReference(user.LogicalName, user.Id);
            return party;
        }
    }
}
