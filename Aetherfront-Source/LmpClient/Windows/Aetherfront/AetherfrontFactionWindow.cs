using Aetherfront.Core;
using LmpClient.Aetherfront;
using LmpClient.Base;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Aetherfront
{
    public sealed class AetherfrontFactionWindow : Window<AetherfrontFactionWindow>
    {
        private const float WindowWidth = 720;
        private const float WindowHeight = 680;
        private bool _display;
        private Vector2 _scrollPosition;
        private string _factionName = string.Empty;
        private string _factionAbbreviation = string.Empty;
        private string _factionDescription = string.Empty;
        private string _factionColor = "#FFFFFF";
        private string _flagAssetId = string.Empty;
        private string _flagSha256 = string.Empty;
        private string _flagPath = string.Empty;
        private string _recruitmentPolicy = RecruitmentPolicies.Application;
        private bool _publicProfile = true;
        private string _profileFactionId = string.Empty;
        private string _targetAccountId = string.Empty;
        private string _targetFactionId = string.Empty;
        private string _amountText = "0";
        private string _tradeScienceText = "0";
        private string _roleText = FactionRoles.Member;
        private string _craftId = string.Empty;
        private string _craftPermission = CraftAccess.ViewFull;
        private string _agreementId = string.Empty;
        private static readonly System.Collections.Generic.Dictionary<string, Texture2D> FlagTextures =
            new System.Collections.Generic.Dictionary<string, Texture2D>(System.StringComparer.OrdinalIgnoreCase);
        private string _selectedFactionId = string.Empty;
        private string _storyTitle = string.Empty;
        private string _storyBody = string.Empty;
        private string _technologyId = string.Empty;
        private string _loanPrincipalText = "0";
        private string _loanIdText = string.Empty;
        private string _loanRepayText = "0";
        private string _researchProjectId = string.Empty;
        private string _researchContributionText = "0";
        private string _tradeFundsText = "0";
        private string _tradeCounterFundsText = "0";
        private string _tradeCounterScienceText = "0";
        private string _tradeCraftId = string.Empty;
        private string _tradeCraftHash = string.Empty;
        private string _tradeCounterCraftId = string.Empty;
        private string _tradeCounterCraftHash = string.Empty;
        private string _objectiveId = string.Empty;
        private string _objectiveSeasonId = string.Empty;
        private string _objectiveTitle = string.Empty;
        private string _objectiveDescription = string.Empty;
        private string _objectiveEventType = string.Empty;
        private string _objectiveTargetText = "1";
        private string _objectiveScienceRewardText = "0";
        private string _objectiveFundsRewardText = "0";
        private string _objectiveReputationRewardText = "0";
        private string _objectiveRepeatDaysText = "0";
        private string _objectiveCatchupDaysText = "0";
        private bool _objectivePublicProgress = true;
        private string _eventModeId = "live-event";
        private string _eventModeName = string.Empty;
        private string _eventModeBodyName = string.Empty;
        private string _eventModeSubspaceText = string.Empty;

        public override bool Display
        {
            get => base.Display && _display && MainSystem.NetworkState >= ClientState.Running &&
                   (HighLogic.LoadedScene == GameScenes.MAINMENU || HighLogic.LoadedScene >= GameScenes.SPACECENTER);
            set => base.Display = _display = value;
        }

        public override void OnDisplay()
        {
            AetherfrontClientSystem.RequestSnapshot();
        }

        protected override void DrawGui()
        {
            WindowRect = FixWindowPos(GUILayout.Window(6730 + MainSystem.WindowOffset, WindowRect, DrawContent, "Aetherfront Factions", LayoutOptions));
        }

        public override void SetStyles()
        {
            WindowRect = new Rect(Screen.width / 2f - WindowWidth / 2f, Screen.height / 2f - WindowHeight / 2f, WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);
            LayoutOptions = new GUILayoutOption[] { GUILayout.MinWidth(WindowWidth), GUILayout.MaxWidth(WindowWidth), GUILayout.MinHeight(WindowHeight), GUILayout.MaxHeight(WindowHeight) };
            TextAreaOptions = new GUILayoutOption[] { GUILayout.ExpandWidth(true) };
        }

        protected override void DrawWindowContent(int windowId)
        {
            GUI.DragWindow(MoveRect);
            var state = AetherfrontClientSystem.State;
            GUILayout.Label("Faction browser and private progression");
            GUILayout.Label("Status: " + AetherfrontClientSystem.Status);
            if (state == null || string.IsNullOrEmpty(state.ServerId))
            {
                if (GUILayout.Button("Refresh")) AetherfrontClientSystem.RequestSnapshot();
                return;
            }

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Width(WindowWidth - 20), GUILayout.Height(WindowHeight - 60));
            var ownFaction = state.FindFactionForAccount(MainSystem.UniqueIdentifier);
            if (ownFaction == null)
            {
                DrawCreateFaction(state);
                DrawInvitations(state);
            }
            else
            {
                DrawOwnFaction(state, ownFaction);
                DrawFactionOperations(state, ownFaction);
            }

            DrawModeratorOperations(state);

            DrawObjectivesAndStandings(state, ownFaction);
            DrawPublicStore(state, ownFaction);
            DrawVesselInterest();

            GUILayout.Space(10);
            GUILayout.Label("Faction browser");
            foreach (var faction in state.Factions)
            {
                if (!faction.PublicProfile || faction.State != FactionStates.Active) continue;
                GUILayout.BeginVertical(Skin.box);
                DrawFactionFlag(faction);
                GUILayout.Label(faction.Name + " [" + faction.Abbreviation + "]");
                GUILayout.Label(faction.Description);
                GUILayout.Label("Members: " + faction.Members.FindAll(m => m.Active).Count + "  Science: " + faction.Progression.Science + "  Achievements: " + faction.Progression.AchievementsCompleted);
                var publicMembers = string.Join(", ", faction.Members.Where(m => m.Active)
                    .Select(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.AccountId : m.DisplayName).Take(8).ToArray());
                if (!string.IsNullOrWhiteSpace(publicMembers)) GUILayout.Label("Roster: " + publicMembers);
                if (ownFaction == null && GUILayout.Button(_selectedFactionId == faction.Id ? "Selected" : "Select")) _selectedFactionId = faction.Id;
                GUILayout.EndVertical();
            }
            GUILayout.Label("Revision: " + state.Revision);
            GUILayout.EndScrollView();
        }

        private void DrawPublicStore(AetherfrontState state, FactionRecord ownFaction)
        {
            var items = state.StoreItems ?? new System.Collections.Generic.List<StoreItem>();
            var visible = items.Where(i => i != null && i.Enabled && i.Public).ToList();
            if (visible.Count == 0) return;
            GUILayout.Space(8);
            GUILayout.Label("Faction public store");
            foreach (var item in visible)
            {
                var owned = ownFaction != null && (ownFaction.Progression.StoreItemIds ?? new System.Collections.Generic.List<string>())
                    .Any(id => string.Equals(id, item.Id, System.StringComparison.OrdinalIgnoreCase));
                GUILayout.BeginVertical(Skin.box);
                GUILayout.Label(item.Title + (owned ? " (owned)" : string.Empty));
                GUILayout.Label(item.Description ?? string.Empty);
                GUILayout.Label("Cost: funds " + item.PriceFundsMicro + " micro; science " + item.PriceScience +
                    "  Stock: " + (item.Stock < 0 ? "unlimited" : item.Stock.ToString()));
                if (ownFaction != null && !owned && item.Stock != 0 && GUILayout.Button("Purchase " + item.Id))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.PurchaseStoreItem,
                        StoreItemId = item.Id
                    });
                GUILayout.EndVertical();
            }
        }

        private void DrawCreateFaction(AetherfrontState state)
        {
            GUILayout.Label("Create a faction");
            _factionName = GUILayout.TextField(_factionName, "Faction name".Length + 32);
            _factionAbbreviation = GUILayout.TextField(_factionAbbreviation, "Abbreviation".Length + 8);
            _factionColor = GUILayout.TextField(_factionColor, "#FFFFFF".Length + 2);
            GUILayout.Label("Recruitment: " + _recruitmentPolicy);
            if (GUILayout.Button("Cycle recruitment policy"))
                _recruitmentPolicy = NextRecruitmentPolicy(_recruitmentPolicy);
            _publicProfile = GUILayout.Toggle(_publicProfile, "Show faction in public browser");
            GUILayout.Label("Flag asset id and SHA-256 (optional)");
            _flagAssetId = GUILayout.TextField(_flagAssetId, 128);
            _flagSha256 = GUILayout.TextField(_flagSha256, 64);
            GUILayout.Label("PNG flag path (absolute or relative to KSP root)");
            _flagPath = GUILayout.TextField(_flagPath, 256);
            _factionDescription = GUILayout.TextArea(_factionDescription, TextAreaOptions);
            if (GUILayout.Button("Create faction"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.CreateFaction,
                    Name = _factionName,
                    Abbreviation = _factionAbbreviation,
                    ColorHex = _factionColor,
                    Description = _factionDescription,
                    FlagAssetId = _flagAssetId,
                    FlagSha256 = _flagSha256,
                    FlagDataBase64 = ReadFlagData(),
                    RecruitmentPolicy = _recruitmentPolicy,
                    Public = _publicProfile,
                    DisplayName = SettingsSystem.CurrentSettings.PlayerName
                });
            if (!string.IsNullOrEmpty(_selectedFactionId) && GUILayout.Button("Join or apply to selected faction"))
            {
                var selected = state.FindFaction(_selectedFactionId);
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = selected != null && selected.RecruitmentPolicy == RecruitmentPolicies.Application
                        ? AetherfrontCommandType.ApplyToFaction : AetherfrontCommandType.JoinFaction,
                    FactionId = _selectedFactionId,
                    DisplayName = SettingsSystem.CurrentSettings.PlayerName
                });
            }
        }

        private void DrawOwnFaction(AetherfrontState state, FactionRecord faction)
        {
            if (_profileFactionId != faction.Id)
            {
                _profileFactionId = faction.Id;
                _factionName = faction.Name;
                _factionAbbreviation = faction.Abbreviation;
                _factionDescription = faction.Description;
                _factionColor = faction.ColorHex;
                _flagAssetId = faction.FlagAssetId;
                _flagSha256 = faction.FlagSha256;
                _recruitmentPolicy = faction.RecruitmentPolicy;
                _publicProfile = faction.PublicProfile;
            }
            GUILayout.Label("Your faction: " + faction.Name + " [" + faction.Abbreviation + "]");
            GUILayout.Label(faction.Description);
            if (!string.IsNullOrWhiteSpace(faction.FlagAssetId))
                GUILayout.Label("Flag asset: " + faction.FlagAssetId);
            GUILayout.Label("Science: " + faction.Progression.Science + "  Funds: " + faction.Progression.FundsMicro + "  Reputation: " + faction.Progression.Reputation);
            GUILayout.Label("Members: " + faction.Members.FindAll(m => m.Active).Count + "  Achievements: " + faction.Progression.AchievementsCompleted);
            GUILayout.Label("Recruitment: " + faction.RecruitmentPolicy + "  Public: " + faction.PublicProfile);
            GUILayout.Label("Edit faction profile");
            _factionName = GUILayout.TextField(_factionName, AetherfrontConstants.MaxFactionNameLength);
            _factionAbbreviation = GUILayout.TextField(_factionAbbreviation, AetherfrontConstants.MaxFactionAbbreviationLength);
            _factionColor = GUILayout.TextField(_factionColor, 7);
            _factionDescription = GUILayout.TextArea(_factionDescription, TextAreaOptions);
            _flagPath = GUILayout.TextField(_flagPath, 256);
            if (GUILayout.Button("Cycle recruitment policy"))
                _recruitmentPolicy = NextRecruitmentPolicy(_recruitmentPolicy);
            _publicProfile = GUILayout.Toggle(_publicProfile, "Show faction in public browser");
            if (GUILayout.Button("Save faction profile"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.UpdateFactionProfile,
                    FactionId = faction.Id,
                    Name = _factionName,
                    Abbreviation = _factionAbbreviation,
                    Description = _factionDescription,
                    ColorHex = _factionColor,
                    FlagAssetId = _flagAssetId,
                    FlagSha256 = _flagSha256,
                    FlagDataBase64 = ReadFlagData(),
                    RecruitmentPolicy = _recruitmentPolicy,
                    Public = _publicProfile
                });
            GUILayout.Label("Technology unlocks: " + (faction.Progression.TechnologyUnlocks.Count == 0 ? "none" : string.Join(", ", faction.Progression.TechnologyUnlocks.ToArray())));
            foreach (var technology in state.TechnologyDefinitions)
            {
                var unlocked = faction.Progression.TechnologyUnlocks.Any(id => string.Equals(id, technology.Id, System.StringComparison.OrdinalIgnoreCase));
                GUILayout.Label(technology.Title + " [" + technology.Id + "] cost " + technology.ScienceCost +
                    (unlocked ? " unlocked" : " locked"));
                if (!unlocked && GUILayout.Button("Select technology " + technology.Id))
                    _technologyId = technology.Id;
            }
            _technologyId = GUILayout.TextField(_technologyId, 96);
            if (GUILayout.Button("Unlock selected technology"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.UnlockTechnology,
                    FactionId = faction.Id,
                    TechnologyId = _technologyId
                });
            GUILayout.Label("Research projects");
            foreach (var project in state.ResearchProjects)
            {
                var periodKey = ResearchProjectPeriodKey(project, System.DateTime.UtcNow);
                if (string.IsNullOrWhiteSpace(periodKey)) continue;
                var progress = state.ResearchProgress.Find(p => string.Equals(p.ProjectId, project.Id, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.FactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(string.IsNullOrWhiteSpace(p.PeriodKey) ? "permanent" : p.PeriodKey, periodKey, System.StringComparison.OrdinalIgnoreCase));
                GUILayout.Label(project.Title + ": " + (progress == null ? 0 : progress.ContributedScience) + "/" + project.TargetScience +
                    (progress != null && progress.Completed ? " complete" : ""));
                if (!string.IsNullOrWhiteSpace(project.Description)) GUILayout.Label(project.Description);
                if (GUILayout.Button("Select research project")) _researchProjectId = project.Id;
            }
            _researchProjectId = GUILayout.TextField(_researchProjectId, 96);
            _researchContributionText = GUILayout.TextField(_researchContributionText, 24);
            if (GUILayout.Button("Contribute faction science to research"))
            {
                var researchAmount = ParseAmount(_researchContributionText);
                if (researchAmount > 0)
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ContributeResearch,
                        FactionId = faction.Id,
                        ResearchProjectId = _researchProjectId,
                        Amount = researchAmount
                    });
            }
            if (GUILayout.Button("Refresh faction data")) AetherfrontClientSystem.RequestSnapshot();
            if (GUILayout.Button("Leave faction"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest { CommandType = AetherfrontCommandType.LeaveFaction });
            GUILayout.Label("Submit story for moderation");
            _storyTitle = GUILayout.TextField(_storyTitle, AetherfrontConstants.MaxStoryTitleLength);
            _storyBody = GUILayout.TextArea(_storyBody, TextAreaOptions);
            if (GUILayout.Button("Submit story"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.PublishStory,
                    FactionId = faction.Id,
                    Title = _storyTitle,
                    Body = _storyBody,
                    Public = true
                });
            foreach (var story in AetherfrontClientSystem.State.Stories.Where(s => string.Equals(s.FactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.PublishedAtUtc).Take(3))
                GUILayout.Label("Story " + story.ModerationState + ": " + story.Title);
        }

        private void DrawInvitations(AetherfrontState state)
        {
            GUILayout.Label("Invitations");
            foreach (var invitation in state.Invitations.Where(i => !i.Accepted && !i.Cancelled &&
                string.Equals(i.AccountId, MainSystem.UniqueIdentifier, System.StringComparison.OrdinalIgnoreCase)))
            {
                var faction = state.FindFaction(invitation.FactionId);
                GUILayout.Label("Invite: " + (faction == null ? invitation.FactionId : faction.Name));
                if (GUILayout.Button("Accept invitation " + invitation.Id.Substring(0, System.Math.Min(8, invitation.Id.Length))))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.AcceptInvitation,
                        InvitationId = invitation.Id,
                        DisplayName = SettingsSystem.CurrentSettings.PlayerName
                    });
            }
        }

        private void DrawModeratorOperations(AetherfrontState state)
        {
            if (state == null || !state.ModeratorAccountIds.Any(id => string.Equals(id, MainSystem.UniqueIdentifier, System.StringComparison.OrdinalIgnoreCase))) return;

            GUILayout.Space(8);
            GUILayout.Label("Game master review");
            foreach (var loan in state.Loans.Where(l => l.State == LoanStates.Pending))
            {
                GUILayout.Label("Pending loan " + loan.Id.Substring(0, System.Math.Min(8, loan.Id.Length)) +
                    " borrower " + loan.BorrowerFactionId + " principal " + loan.PrincipalMicro +
                    " APR " + loan.AprBasisPoints + " bps");
                if (GUILayout.Button("Approve loan " + loan.Id.Substring(0, System.Math.Min(8, loan.Id.Length))))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ApproveLoan,
                        LoanId = loan.Id
                    });
            }

            foreach (var story in state.Stories.Where(s => s.ModerationState == StoryModerationStates.Pending))
            {
                GUILayout.Label("Pending story: " + story.Title);
                if (GUILayout.Button("Approve story"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ReviewStory,
                        StoryId = story.Id,
                        Accept = true
                    });
                if (GUILayout.Button("Reject story"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ReviewStory,
                        StoryId = story.Id,
                        Accept = false
                    });
            }

            GUILayout.Label("Live objective editor");
            GUILayout.Label("ID, season, title, description, event type");
            _objectiveId = GUILayout.TextField(_objectiveId, 96);
            _objectiveSeasonId = GUILayout.TextField(_objectiveSeasonId, 96);
            _objectiveTitle = GUILayout.TextField(_objectiveTitle, AetherfrontConstants.MaxStoryTitleLength);
            _objectiveDescription = GUILayout.TextArea(_objectiveDescription, TextAreaOptions);
            _objectiveEventType = GUILayout.TextField(_objectiveEventType, 96);
            GUILayout.Label("Target, science reward, funds reward (micro), reputation reward");
            _objectiveTargetText = GUILayout.TextField(_objectiveTargetText, 24);
            _objectiveScienceRewardText = GUILayout.TextField(_objectiveScienceRewardText, 24);
            _objectiveFundsRewardText = GUILayout.TextField(_objectiveFundsRewardText, 24);
            _objectiveReputationRewardText = GUILayout.TextField(_objectiveReputationRewardText, 24);
            GUILayout.Label("Repeat days, catch-up days");
            _objectiveRepeatDaysText = GUILayout.TextField(_objectiveRepeatDaysText, 12);
            _objectiveCatchupDaysText = GUILayout.TextField(_objectiveCatchupDaysText, 12);
            _objectivePublicProgress = GUILayout.Toggle(_objectivePublicProgress, "Public progress");
            if (GUILayout.Button("Create or update objective")) SendObjectiveUpsert();
            foreach (var objective in state.Objectives.Where(o => o != null && !o.Archived))
            {
                GUILayout.Label("Objective " + objective.Id + ": " + objective.Title);
                if (GUILayout.Button("Archive objective " + objective.Id))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ArchiveObjective,
                        ObjectiveId = objective.Id
                    });
            }

            GUILayout.Label("Live event mode: temporarily sync all craft at one location");
            if (state.EventMode != null && state.EventMode.Active)
            {
                GUILayout.Label("Active: " + state.EventMode.Name + " / " +
                    (string.IsNullOrWhiteSpace(state.EventMode.BodyName) ? "any body" : state.EventMode.BodyName));
                if (GUILayout.Button("End event mode"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ClearEventMode
                    });
            }
            _eventModeId = GUILayout.TextField(_eventModeId, 96);
            _eventModeName = GUILayout.TextField(_eventModeName, 96);
            _eventModeBodyName = GUILayout.TextField(_eventModeBodyName, 96);
            _eventModeSubspaceText = GUILayout.TextField(_eventModeSubspaceText, 12);
            GUILayout.Label("Mode ID / name / body (blank = any) / subspace (blank = any)");
            if (GUILayout.Button("Start event mode")) SendEventMode();
        }

        private void SendEventMode()
        {
            var subspace = int.MinValue;
            if (!string.IsNullOrWhiteSpace(_eventModeSubspaceText) &&
                !int.TryParse(_eventModeSubspaceText, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out subspace)) return;
            AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
            {
                CommandType = AetherfrontCommandType.SetEventMode,
                EventModeId = _eventModeId,
                EventModeName = _eventModeName,
                EventModeBodyName = _eventModeBodyName,
                EventModeSubspaceId = subspace,
                EventModeAllowAllCrafts = true
            });
        }

        private void SendObjectiveUpsert()
        {
            if (!long.TryParse(_objectiveTargetText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var target) ||
                !long.TryParse(_objectiveScienceRewardText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var science) ||
                !long.TryParse(_objectiveFundsRewardText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var funds) ||
                !long.TryParse(_objectiveReputationRewardText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var reputation) ||
                !int.TryParse(_objectiveRepeatDaysText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var repeatDays) ||
                !int.TryParse(_objectiveCatchupDaysText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var catchupDays))
            {
                GUILayout.Label("Objective values must be whole numbers.");
                return;
            }
            AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
            {
                CommandType = AetherfrontCommandType.UpsertObjective,
                ObjectiveId = _objectiveId,
                SeasonId = _objectiveSeasonId,
                Title = _objectiveTitle,
                Description = _objectiveDescription,
                EventType = _objectiveEventType,
                ObjectiveTarget = target,
                ScienceReward = science,
                FundsRewardMicro = funds,
                ReputationReward = reputation,
                RepeatPeriodDays = repeatDays,
                CatchUpWindowDays = catchupDays,
                ObjectivePublicProgress = _objectivePublicProgress
            });
        }

        private void DrawFactionOperations(AetherfrontState state, FactionRecord faction)
        {
            GUILayout.Space(8);
            GUILayout.Label("Faction members");
            foreach (var member in faction.Members.Where(m => m.Active))
                GUILayout.Label(member.DisplayName + " [" + member.AccountId + "]  " + member.Role);

            _targetAccountId = GUILayout.TextField(_targetAccountId, 96);
            if (GUILayout.Button("Invite account"))
                SendFactionCommand(AetherfrontCommandType.InviteMember, faction.Id, _targetAccountId);
            _roleText = GUILayout.TextField(_roleText, 32);
            if (GUILayout.Button("Set target role"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.SetMemberRole,
                    FactionId = faction.Id,
                    TargetAccountId = _targetAccountId,
                    Role = _roleText
                });
            if (GUILayout.Button("Remove target member"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.RemoveMember,
                    FactionId = faction.Id,
                    TargetAccountId = _targetAccountId
                });
            if (GUILayout.Button("Transfer leadership to target"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.TransferLeadership,
                    FactionId = faction.Id,
                    TargetAccountId = _targetAccountId
                });

            GUILayout.Label("Economy and diplomacy");
            _targetFactionId = GUILayout.TextField(_targetFactionId, 96);
            _amountText = GUILayout.TextField(_amountText, 24);
            if (GUILayout.Button("Transfer science"))
                SendAmountCommand(AetherfrontCommandType.TransferScience, faction.Id, _targetFactionId, _amountText);
            if (GUILayout.Button("Transfer funds"))
                SendAmountCommand(AetherfrontCommandType.TransferFunds, faction.Id, _targetFactionId, _amountText);
            if (GUILayout.Button("Propose alliance"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.ProposeAlliance,
                    FactionId = faction.Id,
                    TargetFactionId = _targetFactionId
                });
            foreach (var agreement in state.Agreements.Where(a => string.Equals(a.ProposerFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.RecipientFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase)))
            {
                GUILayout.Label("Agreement " + agreement.Id.Substring(0, System.Math.Min(8, agreement.Id.Length)) + " " + agreement.Type + " " + agreement.State);
                if (agreement.State == AgreementStates.Proposed && string.Equals(agreement.RecipientFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase) &&
                    GUILayout.Button("Accept agreement"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.RespondToAgreement,
                        AgreementId = agreement.Id,
                        Accept = true
                    });
                if ((agreement.State == AgreementStates.Proposed || agreement.State == AgreementStates.Active) &&
                    string.Equals(agreement.ProposerFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase) && GUILayout.Button("Revoke agreement"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.RevokeAgreement,
                        AgreementId = agreement.Id
                    });
            }

            GUILayout.Label("Recruitment applications");
            foreach (var application in state.Applications.Where(a => a.State == FactionApplicationStates.Pending &&
                string.Equals(a.FactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase)))
            {
                GUILayout.Label(application.DisplayName + " [" + application.AccountId + "]");
                if (GUILayout.Button("Accept application"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ReviewFactionApplication,
                        ApplicationId = application.Id,
                        Accept = true
                    });
                if (GUILayout.Button("Reject application"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ReviewFactionApplication,
                        ApplicationId = application.Id,
                        Accept = false
                    });
            }

            GUILayout.Label("Trade");
            GUILayout.Label("Your offer: funds (micro), science, or registered craft");
            _tradeFundsText = GUILayout.TextField(_tradeFundsText, 24);
            _tradeScienceText = GUILayout.TextField(_tradeScienceText, 24);
            _tradeCraftId = GUILayout.TextField(_tradeCraftId, 96);
            _tradeCraftHash = GUILayout.TextField(_tradeCraftHash, 64);
            GUILayout.Label("Requested in return: funds (micro), science, or registered craft");
            _tradeCounterFundsText = GUILayout.TextField(_tradeCounterFundsText, 24);
            _tradeCounterScienceText = GUILayout.TextField(_tradeCounterScienceText, 24);
            _tradeCounterCraftId = GUILayout.TextField(_tradeCounterCraftId, 96);
            _tradeCounterCraftHash = GUILayout.TextField(_tradeCounterCraftHash, 64);
            if (GUILayout.Button("Create two-sided trade offer"))
            {
                var offered = BuildTradeAssets(_tradeFundsText, _tradeScienceText, _tradeCraftId, _tradeCraftHash);
                var requested = BuildTradeAssets(_tradeCounterFundsText, _tradeCounterScienceText, _tradeCounterCraftId, _tradeCounterCraftHash);
                if (!string.IsNullOrWhiteSpace(_targetFactionId) && (offered.Count > 0 || requested.Count > 0))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.CreateTradeOffer,
                        FactionId = faction.Id,
                        TargetFactionId = _targetFactionId,
                        AssetJson = AetherfrontJson.Serialize(offered),
                        CounterAssetJson = AetherfrontJson.Serialize(requested)
                    });
            }
            foreach (var trade in state.Trades.Where(t => string.Equals(t.ProposerFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.RecipientFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase)))
            {
                GUILayout.Label("Trade " + trade.Id.Substring(0, System.Math.Min(8, trade.Id.Length)) + " " + trade.State);
                GUILayout.Label("Offer: " + FormatTradeAssets(trade.ProposerAssets));
                GUILayout.Label("Request: " + FormatTradeAssets(trade.RecipientAssets));
                if ((trade.State == TradeStates.Proposed || trade.State == TradeStates.PartiallyConfirmed) && GUILayout.Button("Confirm trade"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.ConfirmTrade,
                        TradeId = trade.Id
                    });
                if ((trade.State == TradeStates.Proposed || trade.State == TradeStates.PartiallyConfirmed) && GUILayout.Button("Cancel trade"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.CancelTrade,
                        TradeId = trade.Id
                    });
            }

            GUILayout.Label("Central bank");
            GUILayout.Label("Base APR: " + state.BankPolicy.BaseAprBasisPoints + " bps  Max term: " + state.BankPolicy.MaxTermDays + " days");
            _loanPrincipalText = GUILayout.TextField(_loanPrincipalText, 24);
            if (GUILayout.Button("Request central-bank loan"))
            {
                var principal = ParseAmount(_loanPrincipalText);
                if (principal > 0)
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.RequestLoan,
                        FactionId = faction.Id,
                        Amount = principal,
                        TermDays = 30
                    });
            }
            foreach (var loan in state.Loans.Where(l => string.Equals(l.BorrowerFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase)))
            {
                GUILayout.Label("Loan " + loan.Id.Substring(0, System.Math.Min(8, loan.Id.Length)) + " " + loan.State + " outstanding " + loan.OutstandingMicro);
                _loanIdText = GUILayout.TextField(_loanIdText, 96);
                _loanRepayText = GUILayout.TextField(_loanRepayText, 24);
                if (GUILayout.Button("Repay selected loan"))
                    AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                    {
                        CommandType = AetherfrontCommandType.RepayLoan,
                        LoanId = string.IsNullOrWhiteSpace(_loanIdText) ? loan.Id : _loanIdText,
                        Amount = ParseAmount(_loanRepayText)
                    });
            }

            GUILayout.Label("Craft access");
            _craftId = GUILayout.TextField(_craftId, 96);
            _craftPermission = GUILayout.TextField(_craftPermission, 32);
            if (GUILayout.Button("Grant craft access to target faction"))
                AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.GrantCraftAccess,
                    EntityId = _craftId,
                    TargetFactionId = _targetFactionId,
                    PermissionsJson = AetherfrontJson.Serialize(new System.Collections.Generic.List<string> { _craftPermission })
                });
            foreach (var craft in state.Crafts.Where(c => !c.Archived && string.Equals(c.OwnerFactionId, faction.Id, System.StringComparison.OrdinalIgnoreCase)))
                GUILayout.Label(craft.Name + "  " + craft.Id + "  " + craft.ContentSha256);
        }

        private static long ParseAmount(string text)
        {
            return long.TryParse(text, out var amount) && amount > 0 ? amount : 0;
        }

        private static System.Collections.Generic.List<TradeAsset> BuildTradeAssets(string fundsText, string scienceText,
            string craftId, string craftHash)
        {
            var assets = new System.Collections.Generic.List<TradeAsset>();
            var funds = ParseAmount(fundsText);
            var science = ParseAmount(scienceText);
            if (funds > 0) assets.Add(new TradeAsset { Type = TradeAssetTypes.FundsMicro, Amount = funds });
            if (science > 0) assets.Add(new TradeAsset { Type = TradeAssetTypes.Science, Amount = science });
            if (!string.IsNullOrWhiteSpace(craftId))
                assets.Add(new TradeAsset { Type = TradeAssetTypes.Craft, EntityId = craftId.Trim(), ContentSha256 = craftHash?.Trim() ?? string.Empty });
            return assets;
        }

        private static string FormatTradeAssets(System.Collections.Generic.IEnumerable<TradeAsset> assets)
        {
            var values = (assets ?? System.Linq.Enumerable.Empty<TradeAsset>()).Select(asset =>
            {
                if (asset == null) return "invalid";
                if (asset.Type == TradeAssetTypes.Craft) return "craft " + asset.EntityId;
                return asset.Type + " " + asset.Amount;
            }).ToArray();
            return values.Length == 0 ? "nothing" : string.Join(", ", values);
        }

        private static void SendAmountCommand(AetherfrontCommandType type, string factionId, string targetFactionId, string amountText)
        {
            var amount = ParseAmount(amountText);
            if (amount <= 0) return;
            AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
            {
                CommandType = type,
                FactionId = factionId,
                TargetFactionId = targetFactionId,
                Amount = amount
            });
        }

        private static void SendFactionCommand(AetherfrontCommandType type, string factionId, string targetAccountId)
        {
            AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
            {
                CommandType = type,
                FactionId = factionId,
                TargetAccountId = targetAccountId
            });
        }

        private static string NextRecruitmentPolicy(string current)
        {
            if (string.Equals(current, RecruitmentPolicies.Open, System.StringComparison.OrdinalIgnoreCase)) return RecruitmentPolicies.Application;
            if (string.Equals(current, RecruitmentPolicies.Application, System.StringComparison.OrdinalIgnoreCase)) return RecruitmentPolicies.InviteOnly;
            if (string.Equals(current, RecruitmentPolicies.InviteOnly, System.StringComparison.OrdinalIgnoreCase)) return RecruitmentPolicies.Closed;
            return RecruitmentPolicies.Open;
        }

        private static void DrawFactionFlag(FactionRecord faction)
        {
            if (faction == null || string.IsNullOrWhiteSpace(faction.FlagAssetId)) return;
            if (!FlagTextures.TryGetValue(faction.FlagAssetId, out var texture))
            {
                if (AetherfrontClientSystem.TryGetFlagData(faction.FlagAssetId, out var encoded))
                {
                    try
                    {
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!texture.LoadImage(System.Convert.FromBase64String(encoded))) texture = null;
                        else FlagTextures[faction.FlagAssetId] = texture;
                    }
                    catch { texture = null; }
                }
                else
                    AetherfrontClientSystem.RequestFlag(faction.FlagAssetId);
            }
            if (texture != null) GUILayout.Box(texture, GUILayout.Width(96), GUILayout.Height(60));
        }

        private string ReadFlagData()
        {
            if (string.IsNullOrWhiteSpace(_flagPath)) return string.Empty;
            try
            {
                var path = _flagPath.Trim();
                if (!Path.IsPathRooted(path)) path = Path.Combine(UrlDir.ApplicationRootPath, path);
                if (!File.Exists(path)) return string.Empty;
                var bytes = File.ReadAllBytes(path);
                return bytes.Length > 512 * 1024 ? string.Empty : System.Convert.ToBase64String(bytes);
            }
            catch { return string.Empty; }
        }

        private static void DrawObjectivesAndStandings(AetherfrontState state, FactionRecord ownFaction)
        {
            GUILayout.Space(10);
            GUILayout.Label("Objectives and standings");
            foreach (var objective in state.Objectives)
            {
                if (objective == null || objective.Archived || !objective.PublicProgress) continue;
                var periodKey = ObjectivePeriodKey(objective, System.DateTime.UtcNow);
                var progress = ownFaction == null || string.IsNullOrWhiteSpace(periodKey) ? null :
                    state.ObjectiveProgress.Find(p => p.ObjectiveId == objective.Id && p.FactionId == ownFaction.Id &&
                        string.Equals(string.IsNullOrWhiteSpace(p.PeriodKey) ? "permanent" : p.PeriodKey, periodKey,
                            System.StringComparison.OrdinalIgnoreCase));
                var current = progress == null ? 0 : progress.Progress;
                GUILayout.Label(objective.Title + ": " + current + "/" + objective.Target +
                    (progress != null && progress.Completed ? " complete" : string.Empty));
            }

            var publicFactions = state.Factions.Where(f => f.State == FactionStates.Active && f.PublicProfile).ToList();
            GUILayout.Label("Faction leaderboards");
            DrawLeaderboard("Reputation", publicFactions, f => f.Progression.Reputation, value => value.ToString("N0"));
            DrawLeaderboard("Science earned", publicFactions, f => f.Progression.TotalScienceEarned, value => value.ToString("N0"));
            DrawLeaderboard("Funds earned", publicFactions, f => f.Progression.TotalFundsEarnedMicro,
                value => (value / AetherfrontConstants.FundsMicroPerKspFund).ToString("N0"));
            DrawLeaderboard("Contracts completed", publicFactions, f => f.Progression.ContractsCompleted, value => value.ToString("N0"));
            DrawLeaderboard("Achievements", publicFactions, f => f.Progression.AchievementsCompleted, value => value.ToString("N0"));
            DrawLeaderboard("Successful launches", publicFactions, f => f.Progression.SuccessfulLaunches, value => value.ToString("N0"));
            DrawLeaderboard("Bodies reached", publicFactions, f => f.Progression.BodiesReached, value => value.ToString("N0"));
            DrawLeaderboard("Rescue operations", publicFactions, f => f.Progression.RescueOperations, value => value.ToString("N0"));
            DrawLeaderboard("Research projects", publicFactions, f => f.Progression.ResearchProjectsCompleted, value => value.ToString("N0"));

            GUILayout.Label("Public activity");
            foreach (var item in state.Events.Where(e => e.Public).OrderByDescending(e => e.OccurredAtUtc).Take(8))
                GUILayout.Label(item.OccurredAtUtc.ToLocalTime().ToString("MM-dd HH:mm") + "  " +
                    FactionName(state, item.FactionId) + "  " + item.Type);

            GUILayout.Label("Recent public stories");
            foreach (var story in state.Stories.Where(s => s.Public).OrderByDescending(s => s.PublishedAtUtc).Take(3))
                GUILayout.Label(story.Title + "  [" + story.FactionId + "]");
        }

        private static void DrawVesselInterest()
        {
            GUILayout.Space(10);
            GUILayout.Label("Active vessel activity");
            var interest = AetherfrontClientSystem.VesselInterest;
            if (interest == null || interest.Vessels == null || interest.Vessels.Count == 0)
            {
                GUILayout.Label("No recent vessel activity");
                return;
            }
            foreach (var vessel in interest.Vessels.Take(24))
            {
                var location = string.IsNullOrWhiteSpace(vessel.BodyName) ? "Location pending" : vessel.BodyName;
                var status = vessel.Landed ? "landed" : vessel.Splashed ? "splashed" : "in flight";
                var access = vessel.CanControl ? "control available" : vessel.FullSyncAvailable ? "view available" : "summary only";
                GUILayout.Label(vessel.Name + " | " + vessel.FactionName + " | " + location + " | " + status + " | " + access);
            }
        }

        private static void DrawLeaderboard(string title, System.Collections.Generic.List<FactionRecord> factions,
            System.Func<FactionRecord, long> valueSelector, System.Func<long, string> formatter)
        {
            GUILayout.Label(title);
            var rank = 1;
            foreach (var faction in factions.OrderByDescending(valueSelector).ThenBy(f => f.Name).Take(5))
            {
                GUILayout.Label(rank + ". " + faction.Name + "  " + formatter(valueSelector(faction)));
                rank++;
            }
        }

        private static string FactionName(AetherfrontState state, string factionId)
        {
            var faction = state.FindFaction(factionId);
            return faction == null ? "Public" : faction.Name;
        }

        private static string ObjectivePeriodKey(ObjectiveDefinition objective, System.DateTime nowUtc)
        {
            if (objective == null) return string.Empty;
            if (objective.StartsAtUtc != null && nowUtc < objective.StartsAtUtc.Value) return string.Empty;
            if (objective.EndsAtUtc != null && nowUtc > objective.EndsAtUtc.Value)
            {
                if (objective.CatchUpWindowDays <= 0 || nowUtc > objective.EndsAtUtc.Value.AddDays(objective.CatchUpWindowDays))
                    return string.Empty;
                return (objective.SeasonId ?? objective.Id) + ":catch-up";
            }
            if (objective.RepeatPeriodDays <= 0) return "permanent";
            var anchor = objective.StartsAtUtc ?? new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            var period = (long)System.Math.Floor((nowUtc - anchor).TotalDays / objective.RepeatPeriodDays);
            return (objective.SeasonId ?? objective.Id) + ":" + period;
        }

        private static string ResearchProjectPeriodKey(ResearchProjectDefinition project, System.DateTime nowUtc)
        {
            if (project == null) return string.Empty;
            if (project.StartsAtUtc != null && nowUtc < project.StartsAtUtc.Value) return string.Empty;
            if (project.EndsAtUtc != null && nowUtc > project.EndsAtUtc.Value)
            {
                if (project.CatchUpWindowDays <= 0 || nowUtc > project.EndsAtUtc.Value.AddDays(project.CatchUpWindowDays))
                    return string.Empty;
                return (project.SeasonId ?? project.Id) + ":catch-up";
            }
            if (project.RepeatPeriodDays <= 0) return "permanent";
            var anchor = project.StartsAtUtc ?? new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            var period = (long)System.Math.Floor((nowUtc - anchor).TotalDays / project.RepeatPeriodDays);
            return (project.SeasonId ?? project.Id) + ":" + period;
        }

        public override void RemoveWindowLock()
        {
            if (!IsWindowLocked) return;
            IsWindowLocked = false;
            InputLockManager.RemoveControlLock("AETHERFRONT_FACTIONS");
        }

        public override void CheckWindowLock()
        {
            if (!Display)
            {
                RemoveWindowLock();
                return;
            }
            var mousePosition = Input.mousePosition;
            mousePosition.y = Screen.height - mousePosition.y;
            var shouldLock = WindowRect.Contains(mousePosition);
            if (shouldLock && !IsWindowLocked)
            {
                InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, "AETHERFRONT_FACTIONS");
                IsWindowLocked = true;
            }
            else if (!shouldLock && IsWindowLocked)
                RemoveWindowLock();
        }
    }
}
