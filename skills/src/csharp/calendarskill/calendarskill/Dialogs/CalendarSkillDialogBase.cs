﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CalendarSkill.Models;
using CalendarSkill.Prompts;
using CalendarSkill.Responses.CreateEvent;
using CalendarSkill.Responses.Shared;
using CalendarSkill.Services;
using CalendarSkill.Utilities;
using Luis;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.AI.Luis;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Choices;
using Microsoft.Bot.Builder.LanguageGeneration;
using Microsoft.Bot.Builder.Skills;
using Microsoft.Bot.Builder.Solutions.Authentication;
using Microsoft.Bot.Builder.Solutions.Resources;
using Microsoft.Bot.Builder.Solutions.Responses;
using Microsoft.Bot.Builder.Solutions.Util;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using static Microsoft.Recognizers.Text.Culture;

namespace CalendarSkill.Dialogs
{
    public class CalendarSkillDialogBase : ComponentDialog
    {
        private ConversationState _conversationState;

        public CalendarSkillDialogBase(
            string dialogId,
            BotSettings settings,
            BotServices services,
            ResponseManager responseManager,
            ConversationState conversationState,
            IServiceManager serviceManager,
            IBotTelemetryClient telemetryClient,
            MicrosoftAppCredentials appCredentials)
            : base(dialogId)
        {
            Settings = settings;
            Services = services;
            ResponseManager = responseManager;
            _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));
            Accessor = _conversationState.CreateProperty<CalendarSkillState>(nameof(CalendarSkillState));
            ServiceManager = serviceManager;
            TelemetryClient = telemetryClient;

            AddDialog(new MultiProviderAuthDialog(settings.OAuthConnections, appCredentials));
            AddDialog(new TextPrompt(Actions.Prompt));
            AddDialog(new ConfirmPrompt(Actions.TakeFurtherAction, null, Culture.English) { Style = ListStyle.SuggestedAction });
            AddDialog(new DateTimePrompt(Actions.DateTimePrompt, DateTimeValidator, Culture.English));
            AddDialog(new DateTimePrompt(Actions.DateTimePromptForUpdateDelete, DateTimePromptValidator, Culture.English));
            AddDialog(new ChoicePrompt(Actions.Choice, ChoiceValidator, Culture.English) { Style = ListStyle.None, });
            AddDialog(new ChoicePrompt(Actions.EventChoice, null, Culture.English) { Style = ListStyle.Inline, ChoiceOptions = new ChoiceFactoryOptions { InlineSeparator = string.Empty, InlineOr = string.Empty, InlineOrMore = string.Empty, IncludeNumbers = false } });
            AddDialog(new TimePrompt(Actions.TimePrompt));
            AddDialog(new GetEventPrompt(Actions.GetEventPrompt));
        }

        protected BotSettings Settings { get; set; }

        protected BotServices Services { get; set; }

        protected IStatePropertyAccessor<CalendarSkillState> Accessor { get; set; }

        protected IServiceManager ServiceManager { get; set; }

        protected ResponseManager ResponseManager { get; set; }

        protected override async Task<DialogTurnResult> OnBeginDialogAsync(DialogContext dc, object options, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await Accessor.GetAsync(dc.Context);

            // find contact dialog is not a start dialog, should not run luis part.
            if (state.LuisResult != null && Id != nameof(FindContactDialog))
            {
                await DigestCalendarLuisResult(dc, state.LuisResult, state.GeneralLuisResult, true);
            }

            return await base.OnBeginDialogAsync(dc, options, cancellationToken);
        }

        protected override async Task<DialogTurnResult> OnContinueDialogAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await Accessor.GetAsync(dc.Context);
            if (state.LuisResult != null)
            {
                await DigestCalendarLuisResult(dc, state.LuisResult, state.GeneralLuisResult, false);
            }

            return await base.OnContinueDialogAsync(dc, cancellationToken);
        }

        // Shared steps
        protected async Task<DialogTurnResult> GetAuthToken(WaterfallStepContext sc, CancellationToken cancellationToken)
        {
            try
            {
                return await sc.PromptAsync(nameof(MultiProviderAuthDialog), new PromptOptions());
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> AfterGetAuthToken(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                // When the user authenticates interactively we pass on the tokens/Response event which surfaces as a JObject
                // When the token is cached we get a TokenResponse object.
                if (sc.Result is ProviderTokenResponse providerTokenResponse)
                {
                    var state = await Accessor.GetAsync(sc.Context);
                    state.APIToken = providerTokenResponse.TokenResponse.Token;

                    var provider = providerTokenResponse.AuthenticationProvider;

                    if (provider == OAuthProvider.AzureAD)
                    {
                        state.EventSource = EventSource.Microsoft;
                    }
                    else if (provider == OAuthProvider.Google)
                    {
                        state.EventSource = EventSource.Google;
                    }
                    else
                    {
                        throw new Exception($"The authentication provider \"{provider.ToString()}\" is not support by the Calendar Skill.");
                    }
                }

                return await sc.NextAsync();
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        // Validators
        protected Task<bool> TokenResponseValidator(PromptValidatorContext<Activity> pc, CancellationToken cancellationToken)
        {
            var activity = pc.Recognized.Value;
            if (activity != null && activity.Type == ActivityTypes.Event)
            {
                return Task.FromResult(true);
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        protected Task<bool> AuthPromptValidator(PromptValidatorContext<TokenResponse> promptContext, CancellationToken cancellationToken)
        {
            var token = promptContext.Recognized.Value;
            if (token != null)
            {
                return Task.FromResult(true);
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        protected async Task<bool> ChoiceValidator(PromptValidatorContext<FoundChoice> pc, CancellationToken cancellationToken)
        {
            var state = await Accessor.GetAsync(pc.Context);
            var generalLuisResult = state.GeneralLuisResult;
            var generalTopIntent = generalLuisResult?.TopIntent().intent;
            var calendarLuisResult = state.LuisResult;
            var calendarTopIntent = calendarLuisResult?.TopIntent().intent;

            // TODO: The signature for validators has changed to return bool -- Need new way to handle this logic
            // If user want to show more recipient end current choice dialog and return the intent to next step.
            if (generalTopIntent == Luis.General.Intent.ShowNext || generalTopIntent == Luis.General.Intent.ShowPrevious || calendarTopIntent == CalendarLuis.Intent.ShowNextCalendar || calendarTopIntent == CalendarLuis.Intent.ShowPreviousCalendar)
            {
                // pc.End(topIntent);
                return true;
            }
            else
            {
                if (!pc.Recognized.Succeeded || pc.Recognized == null)
                {
                    // do nothing when not recognized.
                }
                else
                {
                    // pc.End(pc.Recognized.Value);
                    return true;
                }
            }

            return false;
        }

        protected General.Intent? MergeShowIntent(General.Intent? generalIntent, CalendarLuis.Intent? calendarIntent, CalendarLuis calendarLuisResult)
        {
            if (generalIntent == General.Intent.ShowNext || generalIntent == General.Intent.ShowPrevious)
            {
                return generalIntent;
            }

            if (calendarIntent == CalendarLuis.Intent.ShowNextCalendar)
            {
                return General.Intent.ShowNext;
            }

            if (calendarIntent == CalendarLuis.Intent.ShowPreviousCalendar)
            {
                return General.Intent.ShowPrevious;
            }

            if (calendarIntent == CalendarLuis.Intent.FindCalendarEntry)
            {
                if (calendarLuisResult.Entities.OrderReference != null)
                {
                    var orderReference = GetOrderReferenceFromEntity(calendarLuisResult.Entities);
                    if (orderReference == "next")
                    {
                        return General.Intent.ShowNext;
                    }
                }
            }

            return generalIntent;
        }

        protected Task<bool> DateTimePromptValidator(PromptValidatorContext<IList<DateTimeResolution>> promptContext, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        // Helpers
        protected async Task<Activity> GetOverviewMeetingListResponseAsync(
            DialogContext dc,
            ResourceMultiLanguageGenerator lgEngine,
            List<EventModel> events,
            int firstIndex,
            int lastIndex,
            int totalCount,
            int overlapEventCount,
            string templateId,
            object tokens = null)
        {
            var state = await Accessor.GetAsync(dc.Context);
            var eventItemList = await GetMeetingCardListAsync(dc, events);

            var overviewCardParams = new
            {
                listTitle = CalendarCommonStrings.OverviewTitle,
                totalEventCount = totalCount.ToString(),
                overlapEventCount = overlapEventCount.ToString(),
                dateTimeString = state.StartDateString,
                indicator = string.Format(CalendarCommonStrings.ShowMeetingsIndicator, (firstIndex + 1).ToString(), lastIndex.ToString(), totalCount.ToString()),
                userPhoto = await GetMyPhotoUrlAsync(dc.Context),
                provider = string.Format(CalendarCommonStrings.OverviewEventSource, events[0].SourceString()),
                timezone = state.GetUserTimeZone().Id,
                itemData = eventItemList,
                isOverview = true
            };

            if (templateId == null)
            {
                var lgResult = await lgEngine.Generate(dc.Context, "[MeetingListCard]", overviewCardParams);
                var showMeetingPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(dc.Context, lgResult, null);

                return (Activity)showMeetingPrompt;
            }
            else
            {
                var lgResult = await lgEngine.Generate(dc.Context, $"[{templateId}]", tokens);
                var cardResult = await lgEngine.Generate(dc.Context, "[MeetingListCard]", overviewCardParams);
                var showMeetingPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(dc.Context, lgResult + cardResult, null);

                return (Activity)showMeetingPrompt;
            }
        }

        protected async Task<Activity> GetGeneralMeetingListResponseAsync(
            DialogContext dc,
            ResourceMultiLanguageGenerator lgEngine,
            string listTitle,
            List<EventModel> events,
            string templateId,
            object tokens = null,
            int firstIndex = -1,
            int lastIndex = -1,
            int totalCount = -1)
        {
            var state = await Accessor.GetAsync(dc.Context);

            if (firstIndex == -1 || lastIndex == -1 || totalCount == -1)
            {
                firstIndex = 0;
                lastIndex = events.Count;
                totalCount = events.Count;
            }

            var eventItemList = await GetMeetingCardListAsync(dc, events);

            var overviewCardParams = new
            {
                listTitle,
                totalEventCount = 0,
                overlapEventCount = 0,
                dateTimeString = "",
                indicator = string.Format(CalendarCommonStrings.ShowMeetingsIndicator, (firstIndex + 1).ToString(), lastIndex.ToString(), totalCount.ToString()),
                userPhoto = "",
                provider = string.Format(CalendarCommonStrings.OverviewEventSource, events[0].SourceString()),
                timezone = state.GetUserTimeZone().Id,
                itemData = eventItemList,
                isOverview = false
            };

            if (templateId == null)
            {
                var lgResult = await lgEngine.Generate(dc.Context, "[MeetingListCard]", overviewCardParams);
                var showMeetingPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(dc.Context, lgResult, null);

                return (Activity)showMeetingPrompt;
            }
            else
            {
                var lgResult = await lgEngine.Generate(dc.Context, $"[{templateId}]", tokens);
                var cardResult = await lgEngine.Generate(dc.Context, "[MeetingListCard]", overviewCardParams);
                var showMeetingPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(dc.Context, lgResult + cardResult, null);

                return (Activity)showMeetingPrompt;
            }
        }

        protected async Task<Activity> GetDetailMeetingResponseAsync(
            DialogContext dc,
            ResourceMultiLanguageGenerator lgEngine,
            EventModel eventItem,
            string templateId,
            object tokens = null)
        {
            var state = await Accessor.GetAsync(dc.Context);

            var attendeePhotoList = new List<string>();

            foreach (var attendee in eventItem.Attendees)
            {
                attendeePhotoList.Add(await GetUserPhotoUrlAsync(dc.Context, attendee));
            }

            var data = new
            {
                startDateTime = eventItem.StartTime,
                endDateTime = eventItem.EndTime,
                timezone = state.GetUserTimeZone().Id,
                attendees = eventItem.Attendees,
                attendeePhotoList,
                subject = eventItem.Title,
                location = eventItem.Location,
                content = eventItem.Content,
                meetingLink = eventItem.OnlineMeetingUrl
            };

            if (templateId == null)
            {
                var lgResult = await lgEngine.Generate(dc.Context, "[MeetingDetailCard]", data);
                var showMeetingPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(dc.Context, lgResult, null);

                return (Activity)showMeetingPrompt;
            }
            else
            {
                var lgResult = await lgEngine.Generate(dc.Context, $"[{templateId}]", tokens);
                var cardResult = await lgEngine.Generate(dc.Context, "[MeetingDetailCard]", data);
                var showMeetingPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(dc.Context, lgResult + cardResult, null);

                return (Activity)showMeetingPrompt;
            }
        }

        protected async Task<string> GetMyPhotoUrlAsync(ITurnContext context)
        {
            var state = await Accessor.GetAsync(context);
            var token = state.APIToken;
            var service = ServiceManager.InitUserService(token, state.EventSource);

            PersonModel me = null;

            try
            {
                me = await service.GetMeAsync();
                if (me != null && !string.IsNullOrEmpty(me.Photo))
                {
                    return me.Photo;
                }

                var displayName = me == null ? AdaptiveCardHelper.DefaultMe : me.DisplayName ?? me.UserPrincipalName ?? AdaptiveCardHelper.DefaultMe;
                return string.Format(AdaptiveCardHelper.DefaultAvatarIconPathFormat, displayName);
            }
            catch (Exception)
            {
            }

            return string.Format(AdaptiveCardHelper.DefaultAvatarIconPathFormat, AdaptiveCardHelper.DefaultMe);
        }

        protected async Task<string> GetUserPhotoUrlAsync(ITurnContext context, EventModel.Attendee attendee)
        {
            var state = await Accessor.GetAsync(context);
            var token = state.APIToken;
            var service = ServiceManager.InitUserService(token, state.EventSource);
            var displayName = attendee.DisplayName ?? attendee.Address;

            try
            {
                var url = await service.GetPhotoAsync(attendee.Address);
                if (!string.IsNullOrEmpty(url))
                {
                    return url;
                }

                return string.Format(AdaptiveCardHelper.DefaultAvatarIconPathFormat, displayName);
            }
            catch (Exception)
            {
            }

            return string.Format(AdaptiveCardHelper.DefaultAvatarIconPathFormat, displayName);
        }

        protected bool IsRelativeTime(string userInput, string resolverResult, string timex)
        {
            var userInputLower = userInput.ToLower();
            if (userInputLower.Contains(CalendarCommonStrings.Ago) ||
                userInputLower.Contains(CalendarCommonStrings.Before) ||
                userInputLower.Contains(CalendarCommonStrings.Later) ||
                userInputLower.Contains(CalendarCommonStrings.Next))
            {
                return true;
            }

            if (userInputLower.Contains(CalendarCommonStrings.TodayLower) ||
                userInputLower.Contains(CalendarCommonStrings.Now) ||
                userInputLower.Contains(CalendarCommonStrings.YesterdayLower) ||
                userInputLower.Contains(CalendarCommonStrings.TomorrowLower))
            {
                return true;
            }

            if (timex == "PRESENT_REF")
            {
                return true;
            }

            return false;
        }

        protected async Task<List<EventModel>> GetEventsByTime(List<DateTime> startDateList, List<DateTime> startTimeList, List<DateTime> endDateList, List<DateTime> endTimeList, TimeZoneInfo userTimeZone, ICalendarService calendarService)
        {
            // todo: check input datetime is utc
            var rawEvents = new List<EventModel>();
            var resultEvents = new List<EventModel>();

            DateTime? startDate = null;
            if (startDateList.Any())
            {
                startDate = startDateList.Last();
            }

            DateTime? endDate = null;
            if (endDateList.Any())
            {
                endDate = endDateList.Last();
            }

            var searchByStartTime = startTimeList.Any() && endDate == null && !endTimeList.Any();

            startDate = startDate ?? TimeConverter.ConvertUtcToUserTime(DateTime.UtcNow, userTimeZone);
            endDate = endDate ?? startDate ?? TimeConverter.ConvertUtcToUserTime(DateTime.UtcNow, userTimeZone);

            var searchStartTimeList = new List<DateTime>();
            var searchEndTimeList = new List<DateTime>();

            if (startTimeList.Any())
            {
                foreach (var time in startTimeList)
                {
                    searchStartTimeList.Add(TimeZoneInfo.ConvertTimeToUtc(
                        new DateTime(startDate.Value.Year, startDate.Value.Month, startDate.Value.Day, time.Hour, time.Minute, time.Second),
                        userTimeZone));
                }
            }
            else
            {
                searchStartTimeList.Add(TimeZoneInfo.ConvertTimeToUtc(
                    new DateTime(startDate.Value.Year, startDate.Value.Month, startDate.Value.Day), userTimeZone));
            }

            if (endTimeList.Any())
            {
                foreach (var time in endTimeList)
                {
                    searchEndTimeList.Add(TimeZoneInfo.ConvertTimeToUtc(
                        new DateTime(endDate.Value.Year, endDate.Value.Month, endDate.Value.Day, time.Hour, time.Minute, time.Second),
                        userTimeZone));
                }
            }
            else
            {
                searchEndTimeList.Add(TimeZoneInfo.ConvertTimeToUtc(
                    new DateTime(endDate.Value.Year, endDate.Value.Month, endDate.Value.Day, 23, 59, 59), userTimeZone));
            }

            DateTime? searchStartTime = null;

            if (searchByStartTime)
            {
                foreach (var startTime in searchStartTimeList)
                {
                    rawEvents = await calendarService.GetEventsByStartTime(startTime);
                    if (rawEvents.Any())
                    {
                        searchStartTime = startTime;
                        break;
                    }
                }
            }
            else
            {
                for (var i = 0; i < searchStartTimeList.Count(); i++)
                {
                    rawEvents = await calendarService.GetEventsByTime(
                        searchStartTimeList[i],
                        searchEndTimeList.Count() > i ? searchEndTimeList[i] : searchEndTimeList[0]);
                    if (rawEvents.Any())
                    {
                        searchStartTime = searchStartTimeList[i];
                        break;
                    }
                }
            }

            foreach (var item in rawEvents)
            {
                if (item.StartTime >= searchStartTime && item.IsCancelled != true)
                {
                    resultEvents.Add(item);
                }
            }

            return resultEvents;
        }

        protected bool ContainsTime(string timex)
        {
            return timex.Contains("T");
        }

        protected async Task DigestCalendarLuisResult(DialogContext dc, CalendarLuis luisResult, General generalLuisResult, bool isBeginDialog)
        {
            try
            {
                var state = await Accessor.GetAsync(dc.Context);

                var intent = luisResult.TopIntent().intent;

                var entity = luisResult.Entities;

                if (generalLuisResult.Entities.ordinal != null)
                {
                    var value = generalLuisResult.Entities.ordinal[0];
                    var num = int.Parse(value.ToString());
                    state.UserSelectIndex = num - 1;
                }
                else if (generalLuisResult.Entities.number != null)
                {
                    var value = generalLuisResult.Entities.number[0];
                    var num = int.Parse(value.ToString());
                    state.UserSelectIndex = num - 1;
                }

                if (!isBeginDialog)
                {
                    return;
                }

                switch (intent)
                {
                    case CalendarLuis.Intent.FindMeetingRoom:
                    case CalendarLuis.Intent.CreateCalendarEntry:
                        {
                            state.CreateHasDetail = false;
                            if (entity.Subject != null)
                            {
                                state.CreateHasDetail = true;
                                state.Title = GetSubjectFromEntity(entity);
                            }

                            if (entity.personName != null)
                            {
                                state.CreateHasDetail = true;
                                state.AttendeesNameList = GetAttendeesFromEntity(entity, luisResult.Text, state.AttendeesNameList);
                            }

                            if (entity.FromDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (date != null)
                                {
                                    state.CreateHasDetail = true;
                                    state.StartDate = date;
                                }

                                date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (date != null)
                                {
                                    state.CreateHasDetail = true;
                                    state.EndDate = date;
                                }
                            }

                            if (entity.ToDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (date != null)
                                {
                                    state.CreateHasDetail = true;
                                    state.EndDate = date;
                                }
                            }

                            if (entity.FromTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (time != null)
                                {
                                    state.CreateHasDetail = true;
                                    state.StartTime = time;
                                }

                                time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (time != null)
                                {
                                    state.CreateHasDetail = true;
                                    state.EndTime = time;
                                }
                            }

                            if (entity.ToTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (time != null)
                                {
                                    state.CreateHasDetail = true;
                                    state.EndTime = time;
                                }
                            }

                            if (entity.Duration != null)
                            {
                                var duration = GetDurationFromEntity(entity, dc.Context.Activity.Locale);
                                if (duration != -1)
                                {
                                    state.CreateHasDetail = true;
                                    state.Duration = duration;
                                }
                            }

                            if (entity.MeetingRoom != null)
                            {
                                state.CreateHasDetail = true;
                                state.Location = GetMeetingRoomFromEntity(entity);
                            }

                            if (entity.Location != null)
                            {
                                state.CreateHasDetail = true;
                                state.Location = GetLocationFromEntity(entity);
                            }

                            break;
                        }

                    case CalendarLuis.Intent.DeleteCalendarEntry:
                        {
                            if (entity.Subject != null)
                            {
                                state.Title = GetSubjectFromEntity(entity);
                            }

                            if (entity.FromDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (date != null)
                                {
                                    state.StartDate = date;
                                }

                                date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (date != null)
                                {
                                    state.EndDate = date;
                                }
                            }

                            if (entity.FromTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (time != null)
                                {
                                    state.StartTime = time;
                                }

                                time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (time != null)
                                {
                                    state.EndTime = time;
                                }
                            }

                            break;
                        }

                    case CalendarLuis.Intent.ChangeCalendarEntry:
                        {
                            if (entity.Subject != null)
                            {
                                state.Title = GetSubjectFromEntity(entity);
                            }

                            if (entity.FromDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (date != null)
                                {
                                    state.OriginalStartDate = date;
                                }

                                date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (date != null)
                                {
                                    state.OriginalEndDate = date;
                                }
                            }

                            if (entity.ToDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (date != null)
                                {
                                    state.NewStartDate = date;
                                }
                            }

                            if (entity.FromTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (time != null)
                                {
                                    state.OriginalStartTime = time;
                                }

                                time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (time != null)
                                {
                                    state.OriginalEndTime = time;
                                }
                            }

                            if (entity.ToTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (time != null)
                                {
                                    state.NewStartTime = time;
                                }

                                time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (time != null)
                                {
                                    state.NewEndTime = time;
                                }
                            }

                            if (entity.MoveEarlierTimeSpan != null)
                            {
                                state.MoveTimeSpan = GetMoveTimeSpanFromEntity(entity.MoveEarlierTimeSpan[0], dc.Context.Activity.Locale, false);
                            }

                            if (entity.MoveLaterTimeSpan != null)
                            {
                                state.MoveTimeSpan = GetMoveTimeSpanFromEntity(entity.MoveLaterTimeSpan[0], dc.Context.Activity.Locale, true);
                            }

                            if (entity.datetime != null)
                            {
                                var match = entity._instance.datetime.ToList().Find(w => w.Text.ToLower() == CalendarCommonStrings.DailyToken
                                || w.Text.ToLower() == CalendarCommonStrings.WeeklyToken
                                || w.Text.ToLower() == CalendarCommonStrings.MonthlyToken);
                                if (match != null)
                                {
                                    state.RecurrencePattern = match.Text.ToLower();
                                }
                            }

                            break;
                        }

                    case CalendarLuis.Intent.FindCalendarEntry:
                    case CalendarLuis.Intent.FindCalendarDetail:
                    case CalendarLuis.Intent.FindCalendarWhen:
                    case CalendarLuis.Intent.FindCalendarWhere:
                    case CalendarLuis.Intent.FindCalendarWho:
                    case CalendarLuis.Intent.FindDuration:
                        {
                            if (entity.OrderReference != null)
                            {
                                state.OrderReference = GetOrderReferenceFromEntity(entity);
                            }

                            if (entity.FromDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (date != null)
                                {
                                    state.StartDate = date;
                                    state.StartDateString = dateString;
                                }

                                date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (date != null)
                                {
                                    state.EndDate = date;
                                }
                            }

                            if (entity.ToDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (date != null)
                                {
                                    state.EndDate = date;
                                }
                            }

                            if (entity.FromTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (time != null)
                                {
                                    state.StartTime = time;
                                }

                                time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (time != null)
                                {
                                    state.EndTime = time;
                                }
                            }

                            if (entity.ToTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (time != null)
                                {
                                    state.EndTime = time;
                                }
                            }

                            state.AskParameterContent = luisResult.Text;

                            break;
                        }

                    case CalendarLuis.Intent.ConnectToMeeting:
                    case CalendarLuis.Intent.TimeRemaining:
                        {
                            if (entity.FromDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (date != null)
                                {
                                    state.StartDate = date;
                                }
                            }

                            if (entity.ToDate != null)
                            {
                                var dateString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToDate[0]);
                                var date = GetDateFromDateTimeString(dateString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (date != null)
                                {
                                    state.EndDate = date;
                                }
                            }

                            if (entity.FromTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.FromTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), true);
                                if (time != null)
                                {
                                    state.StartTime = time;
                                }

                                time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone(), false);
                                if (time != null)
                                {
                                    state.EndTime = time;
                                }
                            }

                            if (entity.ToTime != null)
                            {
                                var timeString = GetDateTimeStringFromInstanceData(luisResult.Text, entity._instance.ToTime[0]);
                                var time = GetTimeFromDateTimeString(timeString, dc.Context.Activity.Locale, state.GetUserTimeZone());
                                if (time != null)
                                {
                                    state.EndTime = time;
                                }
                            }

                            if (entity.OrderReference != null)
                            {
                                state.OrderReference = GetOrderReferenceFromEntity(entity);
                            }

                            if (entity.Subject != null)
                            {
                                state.Title = entity._instance.Subject[0].Text;
                            }

                            break;
                        }

                    case CalendarLuis.Intent.None:
                        {
                            break;
                        }

                    default:
                        {
                            break;
                        }
                }
            }
            catch
            {
                var state = await Accessor.GetAsync(dc.Context);
                state.Clear();
                await dc.CancelAllDialogsAsync();
                throw;
            }
        }

        protected List<DateTimeResolution> RecognizeDateTime(string dateTimeString, string culture, bool convertToDate = true)
        {
            var results = DateTimeRecognizer.RecognizeDateTime(DateTimeHelper.ConvertNumberToDateTimeString(dateTimeString, convertToDate), culture, options: DateTimeOptions.CalendarMode);

            if (results.Count > 0)
            {
                // Return list of resolutions from first match
                var result = new List<DateTimeResolution>();
                var values = (List<Dictionary<string, string>>)results[0].Resolution["values"];
                foreach (var value in values)
                {
                    result.Add(ReadResolution(value));
                }

                return result;
            }

            return null;
        }

        protected DateTimeResolution ReadResolution(IDictionary<string, string> resolution)
        {
            var result = new DateTimeResolution();

            if (resolution.TryGetValue("timex", out var timex))
            {
                result.Timex = timex;
            }

            if (resolution.TryGetValue("value", out var value))
            {
                result.Value = value;
            }

            if (resolution.TryGetValue("start", out var start))
            {
                result.Start = start;
            }

            if (resolution.TryGetValue("end", out var end))
            {
                result.End = end;
            }

            return result;
        }

        // This method is called by any waterfall step that throws an exception to ensure consistency
        protected async Task HandleDialogExceptions(WaterfallStepContext sc, Exception ex)
        {
            // send trace back to emulator
            var trace = new Activity(type: ActivityTypes.Trace, text: $"DialogException: {ex.Message}, StackTrace: {ex.StackTrace}");
            await sc.Context.SendActivityAsync(trace);

            // log exception
            TelemetryClient.TrackException(ex, new Dictionary<string, string> { { nameof(sc.ActiveDialog), sc.ActiveDialog?.Id } });

            // send error message to bot user
            await sc.Context.SendActivityAsync(ResponseManager.GetResponse(CalendarSharedResponses.CalendarErrorMessage));

            // clear state
            var state = await Accessor.GetAsync(sc.Context);
            state.Clear();
            await sc.CancelAllDialogsAsync();

            return;
        }

        // This method is called by any waterfall step that throws a SkillException to ensure consistency
        protected async Task HandleDialogExceptions(WaterfallStepContext sc, SkillException ex)
        {
            // send trace back to emulator
            var trace = new Activity(type: ActivityTypes.Trace, text: $"DialogException: {ex.Message}, StackTrace: {ex.StackTrace}");
            await sc.Context.SendActivityAsync(trace);

            // log exception
            TelemetryClient.TrackException(ex, new Dictionary<string, string> { { nameof(sc.ActiveDialog), sc.ActiveDialog?.Id } });

            // send error message to bot user
            if (ex.ExceptionType == SkillExceptionType.APIAccessDenied)
            {
                await sc.Context.SendActivityAsync(ResponseManager.GetResponse(CalendarSharedResponses.CalendarErrorMessageBotProblem));
            }
            else
            {
                await sc.Context.SendActivityAsync(ResponseManager.GetResponse(CalendarSharedResponses.CalendarErrorMessage));
            }

            // clear state
            var state = await Accessor.GetAsync(sc.Context);
            state.Clear();
        }

        // This method is called by any waterfall step that throws a SkillException to ensure consistency
        protected async Task HandleExpectedDialogExceptions(WaterfallStepContext sc, Exception ex)
        {
            // send trace back to emulator
            var trace = new Activity(type: ActivityTypes.Trace, text: $"DialogException: {ex.Message}, StackTrace: {ex.StackTrace}");
            await sc.Context.SendActivityAsync(trace);

            // log exception
            TelemetryClient.TrackException(ex, new Dictionary<string, string> { { nameof(sc.ActiveDialog), sc.ActiveDialog?.Id } });
        }

        protected override Task<DialogTurnResult> EndComponentAsync(DialogContext outerDc, object result, CancellationToken cancellationToken)
        {
            var resultString = result?.ToString();
            if (!string.IsNullOrWhiteSpace(resultString) && resultString.Equals(CommonUtil.DialogTurnResultCancelAllDialogs, StringComparison.InvariantCultureIgnoreCase))
            {
                return outerDc.CancelAllDialogsAsync();
            }
            else
            {
                return base.EndComponentAsync(outerDc, result, cancellationToken);
            }
        }

        protected bool IsEmail(string emailString)
        {
            return Regex.IsMatch(emailString, @"\w[-\w.+]*@([A-Za-z0-9][-A-Za-z0-9]+\.)+[A-Za-z]{2,14}");
        }

        protected async Task<string> GetReadyToSendNameListStringAsync(WaterfallStepContext sc)
        {
            var state = await Accessor.GetAsync(sc?.Context);
            var unionList = state.AttendeesNameList.ToList();
            if (unionList.Count == 1)
            {
                return unionList.First();
            }

            var nameString = string.Join(", ", unionList.ToArray().SkipLast(1)) + string.Format(CommonStrings.SeparatorFormat, CommonStrings.And) + unionList.Last();
            return nameString;
        }

        protected (List<PersonModel> formattedPersonList, List<PersonModel> formattedUserList) FormatRecipientList(List<PersonModel> personList, List<PersonModel> userList)
        {
            // Remove dup items
            var formattedPersonList = new List<PersonModel>();
            var formattedUserList = new List<PersonModel>();

            foreach (var person in personList)
            {
                var mailAddress = person.Emails[0] ?? person.UserPrincipalName;
                if (mailAddress == null)
                {
                    continue;
                }

                var isDup = false;
                foreach (var formattedPerson in formattedPersonList)
                {
                    var formattedMailAddress = formattedPerson.Emails[0] ?? formattedPerson.UserPrincipalName;

                    if (mailAddress.Equals(formattedMailAddress))
                    {
                        isDup = true;
                        break;
                    }
                }

                if (!isDup)
                {
                    formattedPersonList.Add(person);
                }
            }

            foreach (var user in userList)
            {
                var mailAddress = user.Emails[0] ?? user.UserPrincipalName;

                var isDup = false;
                foreach (var formattedPerson in formattedPersonList)
                {
                    var formattedMailAddress = formattedPerson.Emails[0] ?? formattedPerson.UserPrincipalName;

                    if (mailAddress.Equals(formattedMailAddress))
                    {
                        isDup = true;
                        break;
                    }
                }

                if (!isDup)
                {
                    foreach (var formattedUser in formattedUserList)
                    {
                        var formattedMailAddress = formattedUser.Emails[0] ?? formattedUser.UserPrincipalName;

                        if (mailAddress.Equals(formattedMailAddress))
                        {
                            isDup = true;
                            break;
                        }
                    }
                }

                if (!isDup)
                {
                    formattedUserList.Add(user);
                }
            }

            return (formattedPersonList, formattedUserList);
        }

        protected async Task<List<PersonModel>> GetContactsAsync(WaterfallStepContext sc, string name)
        {
            var result = new List<PersonModel>();
            var state = await Accessor.GetAsync(sc.Context);
            var token = state.APIToken;
            var service = ServiceManager.InitUserService(token, state.EventSource);

            // Get users.
            result = await service.GetContactsAsync(name);
            return result;
        }

        protected async Task<List<PersonModel>> GetPeopleWorkWithAsync(WaterfallStepContext sc, string name)
        {
            var result = new List<PersonModel>();
            var state = await Accessor.GetAsync(sc.Context);
            var token = state.APIToken;
            var service = ServiceManager.InitUserService(token, state.EventSource);

            // Get users.
            result = await service.GetPeopleAsync(name);

            return result;
        }

        protected async Task<List<PersonModel>> GetUserAsync(WaterfallStepContext sc, string name)
        {
            var result = new List<PersonModel>();
            var state = await Accessor.GetAsync(sc.Context);
            var token = state.APIToken;
            var service = ServiceManager.InitUserService(token, state.EventSource);

            // Get users.
            result = await service.GetUserAsync(name);

            return result;
        }

        protected async Task<PersonModel> GetMe(ITurnContext context)
        {
            var state = await Accessor.GetAsync(context);
            var token = state.APIToken;
            var service = ServiceManager.InitUserService(token, state.EventSource);
            return await service.GetMeAsync();
        }

        protected string GetSelectPromptString(PromptOptions selectOption, bool containNumbers)
        {
            var result = string.Empty;
            result += selectOption.Prompt.Text + "\r\n";
            for (var i = 0; i < selectOption.Choices.Count; i++)
            {
                var choice = selectOption.Choices[i];
                result += "  ";
                if (containNumbers)
                {
                    result += (i + 1) + "-";
                }

                result += choice.Value + "\r\n";
            }

            return result;
        }

        protected async Task<PromptOptions> GenerateOptions(List<PersonModel> personList, List<PersonModel> userList, DialogContext dc)
        {
            var state = await Accessor.GetAsync(dc.Context);
            var pageIndex = state.ShowAttendeesIndex;
            var pageSize = 5;
            var skip = pageSize * pageIndex;
            var options = new PromptOptions
            {
                Choices = new List<Choice>(),
                Prompt = ResponseManager.GetResponse(CreateEventResponses.ConfirmRecipient),
            };
            for (var i = 0; i < personList.Count; i++)
            {
                var user = personList[i];
                var mailAddress = user.Emails[0] ?? user.UserPrincipalName;

                var choice = new Choice()
                {
                    Value = $"**{user.DisplayName}: {mailAddress}**",
                    Synonyms = new List<string> { (options.Choices.Count + 1).ToString(), user.DisplayName, user.DisplayName.ToLower(), mailAddress },
                };

                var userName = user.UserPrincipalName?.Split("@").FirstOrDefault() ?? user.UserPrincipalName;
                if (!string.IsNullOrEmpty(userName))
                {
                    choice.Synonyms.Add(userName);
                    choice.Synonyms.Add(userName.ToLower());
                }

                if (skip <= 0)
                {
                    if (options.Choices.Count >= pageSize)
                    {
                        return options;
                    }

                    options.Choices.Add(choice);
                }
                else
                {
                    skip--;
                }
            }

            if (options.Choices.Count == 0)
            {
                pageSize = 10;
            }

            for (var i = 0; i < userList.Count; i++)
            {
                var user = userList[i];
                var mailAddress = user.Emails[0] ?? user.UserPrincipalName;
                var choice = new Choice()
                {
                    Value = $"{user.DisplayName}: {mailAddress}",
                    Synonyms = new List<string> { (options.Choices.Count + 1).ToString(), user.DisplayName, user.DisplayName.ToLower(), mailAddress },
                };

                var userName = user.UserPrincipalName?.Split("@").FirstOrDefault() ?? user.UserPrincipalName;
                if (!string.IsNullOrEmpty(userName))
                {
                    choice.Synonyms.Add(userName);
                    choice.Synonyms.Add(userName.ToLower());
                }

                if (skip <= 0)
                {
                    if (options.Choices.Count >= pageSize)
                    {
                        return options;
                    }

                    options.Choices.Add(choice);
                }
                else if (skip >= 10)
                {
                    return options;
                }
                else
                {
                    skip--;
                }
            }

            return options;
        }

        protected string GetSubjectFromEntity(CalendarLuis._Entities entity)
        {
            return entity.Subject[0];
        }

        protected List<string> GetAttendeesFromEntity(CalendarLuis._Entities entity, string inputString, List<string> attendees = null)
        {
            if (attendees == null)
            {
                attendees = new List<string>();
            }

            // As luis result for email address often contains extra spaces for word breaking
            // (e.g. send email to test@test.com, email address entity will be test @ test . com)
            // So use original user input as email address.
            var rawEntity = entity._instance.personName;
            foreach (var name in rawEntity)
            {
                var contactName = inputString.Substring(name.StartIndex, name.EndIndex - name.StartIndex);
                if (!attendees.Contains(contactName))
                {
                    attendees.Add(contactName);
                }
            }

            return attendees;
        }

        // Workaround until adaptive card renderer in teams is upgraded to v1.2
        protected string GetDivergedCardName(ITurnContext turnContext, string card)
        {
            if (Channel.GetChannelId(turnContext) == Channels.Msteams)
            {
                return card + ".1.0";
            }
            else
            {
                return card;
            }
        }

        private async Task<string> GetPhotoByIndexAsync(ITurnContext context, List<EventModel.Attendee> attendees, int index)
        {
            if (attendees.Count <= index)
            {
                return AdaptiveCardHelper.BlankIcon;
            }

            return await GetUserPhotoUrlAsync(context, attendees[index]);
        }

        private async Task<List<object>> GetMeetingCardListAsync(DialogContext dc, List<EventModel> events)
        {
            var state = await Accessor.GetAsync(dc.Context);

            var eventItemList = new List<object>();

            DateTime? currentAddedDateUser = null;
            foreach (var item in events)
            {
                var itemDateUser = TimeConverter.ConvertUtcToUserTime(item.StartTime, state.GetUserTimeZone());
                if (currentAddedDateUser == null || !currentAddedDateUser.Value.Date.Equals(itemDateUser.Date))
                {
                    currentAddedDateUser = itemDateUser;
                    eventItemList.Add(new
                    {
                        Name = "CalendarDate",
                        Date = item.StartTime
                    });
                }

                eventItemList.Add(new
                {
                    Name = "CalendarItem",
                    Event = item
                    //item.ToAdaptiveCardData(state.GetUserTimeZone())
                });
            }

            return eventItemList;
        }

        private int GetDurationFromEntity(CalendarLuis._Entities entity, string local)
        {
            var culture = local ?? English;
            var result = RecognizeDateTime(entity.Duration[0], culture);
            if (result != null)
            {
                if (result[0].Value != null)
                {
                    return int.Parse(result[0].Value);
                }
            }

            return -1;
        }

        private int GetMoveTimeSpanFromEntity(string timeSpan, string local, bool later)
        {
            var culture = local ?? English;
            var result = RecognizeDateTime(timeSpan, culture);
            if (result != null)
            {
                if (result[0].Value != null)
                {
                    if (later)
                    {
                        return int.Parse(result[0].Value);
                    }
                    else
                    {
                        return -int.Parse(result[0].Value);
                    }
                }
            }

            return 0;
        }

        private string GetMeetingRoomFromEntity(CalendarLuis._Entities entity)
        {
            return entity.MeetingRoom[0];
        }

        private string GetLocationFromEntity(CalendarLuis._Entities entity)
        {
            return entity.Location[0];
        }

        private string GetDateTimeStringFromInstanceData(string inputString, InstanceData data)
        {
            return inputString.Substring(data.StartIndex, data.EndIndex - data.StartIndex);
        }

        private List<DateTime> GetDateFromDateTimeString(string date, string local, TimeZoneInfo userTimeZone, bool isStart = true)
        {
            var culture = local ?? English;
            var results = RecognizeDateTime(date, culture, true);
            var dateTimeResults = new List<DateTime>();
            if (results != null)
            {
                foreach (var result in results)
                {
                    if (result.Value != null)
                    {
                        if (!isStart)
                        {
                            break;
                        }

                        var dateTime = DateTime.Parse(result.Value);
                        var dateTimeConvertType = result.Timex;

                        if (dateTime != null)
                        {
                            var isRelativeTime = IsRelativeTime(date, result.Value, result.Timex);
                            dateTimeResults.Add(isRelativeTime ? TimeZoneInfo.ConvertTime(dateTime, TimeZoneInfo.Local, userTimeZone) : dateTime);
                        }
                    }
                    else
                    {
                        var startDate = DateTime.Parse(result.Start);
                        var endDate = DateTime.Parse(result.End);
                        if (isStart)
                        {
                            dateTimeResults.Add(startDate);
                        }
                        else
                        {
                            dateTimeResults.Add(endDate);
                        }
                    }
                }
            }

            return dateTimeResults;
        }

        private List<DateTime> GetTimeFromDateTimeString(string time, string local, TimeZoneInfo userTimeZone, bool isStart = true)
        {
            var culture = local ?? English;
            var results = RecognizeDateTime(time, culture, false);
            var dateTimeResults = new List<DateTime>();
            if (results != null)
            {
                foreach (var result in results)
                {
                    if (result.Value != null)
                    {
                        if (!isStart)
                        {
                            break;
                        }

                        var dateTime = DateTime.Parse(result.Value);
                        var dateTimeConvertType = result.Timex;

                        if (dateTime != null)
                        {
                            var isRelativeTime = IsRelativeTime(time, result.Value, result.Timex);
                            dateTimeResults.Add(isRelativeTime ? TimeZoneInfo.ConvertTime(dateTime, TimeZoneInfo.Local, userTimeZone) : dateTime);
                        }
                    }
                    else
                    {
                        var startTime = DateTime.Parse(result.Start);
                        var endTime = DateTime.Parse(result.End);
                        if (isStart)
                        {
                            dateTimeResults.Add(startTime);
                        }
                        else
                        {
                            dateTimeResults.Add(endTime);
                        }
                    }
                }
            }

            return dateTimeResults;
        }

        private string GetOrderReferenceFromEntity(CalendarLuis._Entities entity)
        {
            return entity.OrderReference[0];
        }

        /// <summary>
        /// implement the basic validation. Advanced validation done in upper level dialogs.
        /// </summary>
        /// <param name="prompt">datetime prompt.</param>
        /// <param name="cancellationToken">cancellation token.</param>
        /// <returns>validation result.</returns>
        private Task<bool> DateTimeValidator(PromptValidatorContext<IList<DateTimeResolution>> prompt, CancellationToken cancellationToken)
        {
            if (prompt.Recognized.Succeeded)
            {
                return Task.FromResult(true);
            }
            else
            {
                return Task.FromResult(false);
            }
        }
    }
}