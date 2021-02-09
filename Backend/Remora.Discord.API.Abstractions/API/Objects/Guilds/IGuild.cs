//
//  IGuild.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2017 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Remora.Discord.Core;

namespace Remora.Discord.API.Abstractions.Objects
{
    /// <summary>
    /// Represents a Discord Guild.
    /// </summary>
    [PublicAPI]
    public interface IGuild
    {
        /// <summary>
        /// Gets the ID of the guild.
        /// </summary>
        Snowflake ID { get; }

        /// <summary>
        /// Gets the name of the guild.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the guild's icon.
        /// </summary>
        IImageHash? Icon { get; }

        /// <summary>
        /// Gets the guild's splash banner.
        /// </summary>
        IImageHash? Splash { get; }

        /// <summary>
        /// Gets the guild's Discovery splash banner.
        /// </summary>
        IImageHash? DiscoverySplash { get; }

        /// <summary>
        /// Gets a value indicating whether the current user is the guild's owner.
        /// </summary>
        Optional<bool> IsOwner { get; }

        /// <summary>
        /// Gets the ID of the owner.
        /// </summary>
        Snowflake OwnerID { get; }

        /// <summary>
        /// Gets the permissions for the current user in the guild.
        /// </summary>
        Optional<IDiscordPermissionSet> Permissions { get; }

        /// <summary>
        /// Gets the unique ID of the voice region.
        /// </summary>
        string Region { get; }

        /// <summary>
        /// Gets the ID of the AFK channel.
        /// </summary>
        Snowflake? AFKChannelID { get; }

        /// <summary>
        /// Gets the AFK timeout (in seconds).
        /// </summary>
        TimeSpan AFKTimeout { get; }

        /// <summary>
        /// Gets the verification level required for the guild.
        /// </summary>
        VerificationLevel VerificationLevel { get; }

        /// <summary>
        /// Gets the default notification level for the guild.
        /// </summary>
        MessageNotificationLevel DefaultMessageNotifications { get; }

        /// <summary>
        /// Gets the explicit content level.
        /// </summary>
        ExplicitContentFilterLevel ExplicitContentFilter { get; }

        /// <summary>
        /// Gets a list of the roles in the server.
        /// </summary>
        IReadOnlyList<IRole> Roles { get; }

        /// <summary>
        /// Gets a list of emojis in the server.
        /// </summary>
        IReadOnlyList<IEmoji> Emojis { get; }

        /// <summary>
        /// Gets a list of guild features.
        /// </summary>
        IReadOnlyList<string> GuildFeatures { get; }

        /// <summary>
        /// Gets the required MFA level for the guild.
        /// </summary>
        MultiFactorAuthenticationLevel MFALevel { get; }

        /// <summary>
        /// Gets the application ID of the guild creator if it is bot-created.
        /// </summary>
        Snowflake? ApplicationID { get; }

        /// <summary>
        /// Gets a value indicating whether the server widget is enabled.
        /// </summary>
        Optional<bool> IsWidgetEnabled { get; }

        /// <summary>
        /// Gets the ID of the channel the widget generates invites to.
        /// </summary>
        Optional<Snowflake?> WidgetChannelID { get; }

        /// <summary>
        /// Gets the ID of the channel that system messages are sent to.
        /// </summary>
        Snowflake? SystemChannelID { get; }

        /// <summary>
        /// Gets the flags on the system channel.
        /// </summary>
        SystemChannelFlags SystemChannelFlags { get; }

        /// <summary>
        /// Gets the ID of the rules channel, if any. This is the channel where community-enabled guilds can display
        /// rules and/or guidelines.
        /// </summary>
        Snowflake? RulesChannelID { get; }

        /// <summary>
        /// Gets the time when the current user joined the guild.
        /// </summary>
        Optional<DateTimeOffset> JoinedAt { get; }

        /// <summary>
        /// Gets a value indicating whether this is considered a large guild.
        /// </summary>
        Optional<bool> IsLarge { get; }

        /// <summary>
        /// Gets a value indicating whether the guild is unavailable due to an outage.
        /// </summary>
        Optional<bool> IsUnavailable { get; }

        /// <summary>
        /// Gets the number of members in the guild.
        /// </summary>
        Optional<int> MemberCount { get; }

        /// <summary>
        /// Gets the states of members currently in voice channels.
        /// </summary>
        Optional<IReadOnlyList<IPartialVoiceState>> VoiceStates { get; }

        /// <summary>
        /// Gets the members in the guild.
        /// </summary>
        Optional<IReadOnlyList<IGuildMember>> Members { get; }

        /// <summary>
        /// Gets the channels in the guild.
        /// </summary>
        Optional<IReadOnlyList<IChannel>> Channels { get; }

        /// <summary>
        /// Gets the presences of the members in the guild.
        /// </summary>
        Optional<IReadOnlyList<IPartialPresence>> Presences { get; }

        /// <summary>
        /// Gets the maximum number of presences for the guild. The default value (currently 25000) is in effect when
        /// null is returned.
        /// </summary>
        Optional<int?> MaxPresences { get; }

        /// <summary>
        /// Gets the maximum number of members for the guild.
        /// </summary>
        Optional<int> MaxMembers { get; }

        /// <summary>
        /// Gets the vanity url code for the guild.
        /// </summary>
        string? VanityUrlCode { get; }

        /// <summary>
        /// Gets the description of the guild, if the guild is discoverable.
        /// </summary>
        string? Description { get; }

        /// <summary>
        /// Gets the hash of the guild banner.
        /// </summary>
        IImageHash? Banner { get; }

        /// <summary>
        /// Gets the boost level of the guild.
        /// </summary>
        PremiumTier PremiumTier { get; }

        /// <summary>
        /// Gets the number of boosts the guild currently has.
        /// </summary>
        Optional<int> PremiumSubscriptionCount { get; }

        /// <summary>
        /// Gets the preferred locale of a public-enabled guild.
        /// </summary>
        string PreferredLocale { get; }

        /// <summary>
        /// Gets the ID of the channel where admins and moderators of community-enabled guilds receive notices from
        /// Discord.
        /// </summary>
        Snowflake? PublicUpdatesChannelID { get; }

        /// <summary>
        /// Gets the maximum number of users in a video channel.
        /// </summary>
        Optional<int> MaxVideoChannelUsers { get; }

        /// <summary>
        /// Gets the approximate number of members in the guild.
        /// </summary>
        Optional<int> ApproximateMemberCount { get; }

        /// <summary>
        /// Gets the approximate number of non-offline members in the guild.
        /// </summary>
        Optional<int> ApproximatePresenceCount { get; }

        /// <summary>
        /// Gets the welcome screen shown to new members.
        /// </summary>
        Optional<IWelcomeScreen> WelcomeScreen { get; }
    }
}
