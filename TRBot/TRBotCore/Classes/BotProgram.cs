﻿/* This file is part of TRBot.
 *
 * TRBot is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * TRBot is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with TRBot.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.Client.Events;
using TwitchLib.Communication.Events;
using TwitchLib.Communication.Interfaces;
using Newtonsoft;
using Newtonsoft.Json;

namespace TRBot
{
    public sealed class BotProgram : IDisposable
    {
        private static readonly object BotDataLockObj = new object();
        private static readonly object SettingsLockObj = new object();

        private static BotProgram instance = null;

        public bool Initialized { get; private set; } = false;

        private LoginInfo LoginInformation = null;
        public static Settings BotSettings { get; private set; } = null;
        public static BotData BotData { get; private set; } = null;

        public static IClientService ClientService { get; private set; } = null;
        private ConnectionCredentials Credentials = null;
        private CrashHandler crashHandler = null;

        private CommandHandler CommandHandler = null;

        public static bool TryReconnect { get; private set; } = false;
        public static bool ChannelJoined { get; private set; } = false;

        public bool IsInChannel => (ClientService?.IsConnected == true && ChannelJoined == true);

        private DateTime CurQueueTime;

        /// <summary>
        /// Queued messages.
        /// </summary>
        private Queue<string> ClientMessages = new Queue<string>();

        private List<BaseRoutine> BotRoutines = new List<BaseRoutine>();

        /// <summary>
        /// Whether to ignore logging bot messages to the console based on potential console logs from the <see cref="ExecCommand"/>.
        /// </summary>
        public static bool IgnoreConsoleLog = false;

        public static string BotName
        {
            get
            {
                if (instance != null)
                {
                    if (instance.LoginInformation != null) return instance.LoginInformation.BotName;
                }

                return "N/A";
            }
        }

        public BotProgram()
        {
            crashHandler = new CrashHandler();
            
            instance = this;

            //Below normal priority
            Process thisProcess = Process.GetCurrentProcess();
            thisProcess.PriorityBoostEnabled = false;
            thisProcess.PriorityClass = ProcessPriorityClass.Idle;
        }

        //Clean up anything we need to here
        public void Dispose()
        {
            if (Initialized == false)
                return;

            UnsubscribeEvents();

            for (int i = 0; i < BotRoutines.Count; i++)
            {
                BotRoutines[i].CleanUp(ClientService);
            }

            CommandHandler.CleanUp();
            ClientService?.CleanUp();

            ClientMessages.Clear();

            if (ClientService?.IsConnected == true)
                ClientService.Disconnect();

            //Clean up and relinquish the devices when we're done
            InputGlobals.ControllerMngr?.CleanUp();

            instance = null;
        }

        public void Initialize()
        {
            if (Initialized == true)
                return;

            //Kimimaru: Use invariant culture
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            //Load all the necessary data; if something doesn't exist, save out an empty object so it can be filled in manually
            string loginText = Globals.ReadFromTextFileOrCreate(Globals.LoginInfoFilename);
            LoginInformation = JsonConvert.DeserializeObject<LoginInfo>(loginText);

            if (LoginInformation == null)
            {
                Console.WriteLine("No login information found; attempting to create file template. If created, please manually fill out the information.");

                LoginInformation = new LoginInfo();
                string text = JsonConvert.SerializeObject(LoginInformation, Formatting.Indented);
                Globals.SaveToTextFile(Globals.LoginInfoFilename, text);
            }

            LoadSettingsAndBotData();

            //Kimimaru: If the bot itself isn't in the bot data, add it as an admin!
            if (string.IsNullOrEmpty(LoginInformation.BotName) == false)
            {
                string botName = LoginInformation.BotName.ToLowerInvariant();
                User botUser = null;
                if (BotData.Users.TryGetValue(botName, out botUser) == false)
                {
                    botUser = new User();
                    botUser.Name = botName;
                    botUser.Level = (int)AccessLevels.Levels.Admin;
                    BotData.Users.TryAdd(botName, botUser);

                    SaveBotData();
                }
            }

            try
            {
                Credentials = new ConnectionCredentials(LoginInformation.BotName, LoginInformation.Password);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Invalid credentials: {exception.Message}");
                Console.WriteLine("Cannot proceed. Please double check the login information in the data folder");
                return;
            }
            
            //Set up client service
            ClientService = new TwitchClientService(Credentials, LoginInformation.ChannelName, Globals.CommandIdentifier,
                Globals.CommandIdentifier, true);

            ClientService.Initialize();

            UnsubscribeEvents();

            ClientService.EventHandler.UserSentMessageEvent += OnUserSentMessage;
            ClientService.EventHandler.UserMadeInputEvent += OnUserMadeInput;
            ClientService.EventHandler.UserNewlySubscribedEvent += OnNewSubscriber;
            ClientService.EventHandler.UserReSubscribedEvent += OnReSubscriber;
            ClientService.EventHandler.WhisperReceivedEvent += OnWhisperReceived;
            ClientService.EventHandler.ChatCommandReceivedEvent += OnChatCommandReceived;
            ClientService.EventHandler.OnJoinedChannelEvent += OnJoinedChannel;
            ClientService.EventHandler.ChannelHostedEvent += OnBeingHosted;
            ClientService.EventHandler.OnConnectedEvent += OnConnected;
            ClientService.EventHandler.OnConnectionErrorEvent += OnConnectionError;
            ClientService.EventHandler.OnReconnectedEvent += OnReconnected;
            ClientService.EventHandler.OnDisconnectedEvent += OnDisconnected;

            AddRoutine(new PeriodicMessageRoutine());
            AddRoutine(new CreditsGiveRoutine());
            AddRoutine(new ReconnectRoutine());
            AddRoutine(new ChatBotResponseRoutine());

            //Initialize controller input - validate the controller type first
            if (InputGlobals.IsVControllerSupported((InputGlobals.VControllerTypes)BotData.LastVControllerType) == false)
            {
                BotData.LastVControllerType = (int)InputGlobals.GetDefaultSupportedVControllerType();
            }

            InputGlobals.VControllerTypes vCType = (InputGlobals.VControllerTypes)BotData.LastVControllerType;
            Console.WriteLine($"Setting up virtual controller {vCType}");
            
            InputGlobals.SetVirtualController(vCType);

            Initialized = true;
        }

        public void Run()
        {
            if (ClientService.IsConnected == true)
            {
                Console.WriteLine("Client is already connected and running!");
                return;
            }

            ClientService.Connect();

            //Run
            while (true)
            {
                DateTime now = DateTime.Now;

                TimeSpan queueDiff = now - CurQueueTime;

                //Queued messages
                if (ClientMessages.Count > 0 && queueDiff.TotalMilliseconds >= BotSettings.MessageCooldown)
                {
                    if (IsInChannel == true)
                    {
                        string message = ClientMessages.Dequeue();

                        //There's a chance the bot could be disconnected from the channel between the conditional and now
                        try
                        {
                            //Send the message
                            ClientService.SendMessage(LoginInformation.ChannelName, message);
                        }
                        catch (TwitchLib.Client.Exceptions.BadStateException e)
                        {
                            Console.WriteLine($"Could not send message due to bad state: {e.Message}");
                        }

                        if (IgnoreConsoleLog == false)
                        {
                            Console.WriteLine(message);
                        }

                        CurQueueTime = now;
                    }
                }

                //Update routines
                for (int i = 0; i < BotRoutines.Count; i++)
                {
                    if (BotRoutines[i] == null)
                    {
                        Console.WriteLine($"NULL BOT ROUTINE AT {i} SOMEHOW!!");
                        continue;
                    }

                    BotRoutines[i].UpdateRoutine(ClientService, now);
                }

                Thread.Sleep(BotSettings.MainThreadSleep);
            }
        }

        public static void QueueMessage(string message)
        {
            if (string.IsNullOrEmpty(message) == false)
            {
                instance.ClientMessages.Enqueue(message);
            }
        }

        public static void AddRoutine(BaseRoutine routine)
        {
            routine.Initialize(ClientService);
            instance.BotRoutines.Add(routine);
        }

        public static void RemoveRoutine(BaseRoutine routine)
        {
            routine.CleanUp(ClientService);
            instance.BotRoutines.Remove(routine);
        }

        public static BaseRoutine FindRoutine<T>()
        {
            return instance.BotRoutines.Find((routine) => routine is T);
        }

        private void UnsubscribeEvents()
        {
            ClientService.EventHandler.UserSentMessageEvent -= OnUserSentMessage;
            ClientService.EventHandler.UserMadeInputEvent -= OnUserMadeInput;
            ClientService.EventHandler.UserNewlySubscribedEvent -= OnNewSubscriber;
            ClientService.EventHandler.UserReSubscribedEvent -= OnReSubscriber;
            ClientService.EventHandler.WhisperReceivedEvent -= OnWhisperReceived;
            ClientService.EventHandler.ChatCommandReceivedEvent -= OnChatCommandReceived;
            ClientService.EventHandler.OnJoinedChannelEvent -= OnJoinedChannel;
            ClientService.EventHandler.ChannelHostedEvent -= OnBeingHosted;
            ClientService.EventHandler.OnConnectedEvent -= OnConnected;
            ClientService.EventHandler.OnConnectionErrorEvent -= OnConnectionError;
            ClientService.EventHandler.OnReconnectedEvent -= OnReconnected;
            ClientService.EventHandler.OnDisconnectedEvent -= OnDisconnected;
        }

#region Events

        private void OnConnected(OnConnectedArgs e)
        {
            TryReconnect = false;
            ChannelJoined = false;

            Console.WriteLine($"{LoginInformation.BotName} connected!");
        }

        private void OnConnectionError(OnConnectionErrorArgs e)
        {
            ChannelJoined = false;

            if (TryReconnect == false)
            {
                Console.WriteLine($"Failed to connect: {e.Error.Message}");

                TryReconnect = true;
            }
        }

        private void OnJoinedChannel(OnJoinedChannelArgs e)
        {
            if (string.IsNullOrEmpty(BotSettings.ConnectMessage) == false)
            {
                string finalMsg = BotSettings.ConnectMessage.Replace("{0}", LoginInformation.BotName).Replace("{1}", Globals.CommandIdentifier.ToString());
                QueueMessage(finalMsg);
            }

            Console.WriteLine($"Joined channel \"{e.Channel}\"");

            TryReconnect = false;
            ChannelJoined = true;

            if (CommandHandler == null)
            {
                CommandHandler = new CommandHandler();
            }
        }

        private void OnChatCommandReceived(OnChatCommandReceivedArgs e)
        {
            CommandHandler.HandleCommand(e);
        }

        private void OnUserSentMessage(User user, OnMessageReceivedArgs e)
        {
            if (user.OptedOut == false)
            {
                user.IncrementMsgCount();
            }

            string possibleMeme = e.ChatMessage.Message.ToLower();
            if (BotProgram.BotData.Memes.TryGetValue(possibleMeme, out string meme) == true)
            {
                BotProgram.QueueMessage(meme);
            }
        }

        private void OnUserMadeInput(User user, in Parser.InputSequence validInputSeq)
        {
            //Mark this as a valid input
            if (user.OptedOut == false)
            {
                user.IncrementValidInputCount();
            }

            bool shouldPerformInput = true;

            //Check the team the user is on for the controller they should be using
            //Validate that the controller is acquired and exists
            int controllerNum = user.Team;
            if (controllerNum < 0 || controllerNum >= InputGlobals.ControllerMngr.ControllerCount)
            {
                BotProgram.QueueMessage($"ERROR: Invalid joystick number {controllerNum + 1}. # of joysticks: {InputGlobals.ControllerMngr.ControllerCount}. Please change your controller port to a valid number to perform inputs.");
                shouldPerformInput = false;
            }
            //Now verify that the controller has been acquired
            else if (InputGlobals.ControllerMngr.GetController(controllerNum).IsAcquired == false)
            {
                BotProgram.QueueMessage($"ERROR: Joystick number {controllerNum + 1} with controller ID of {InputGlobals.ControllerMngr.GetController(controllerNum).ControllerID} has not been acquired! Ensure you (the streamer) have a virtual device set up at this ID.");
                shouldPerformInput = false;
            }
            //We're okay to perform the input
            if (shouldPerformInput == true)
            {
                InputHandler.CarryOutInput(validInputSeq.Inputs, controllerNum);

                //If auto whitelist is enabled, the user reached the whitelist message threshold,
                //the user isn't whitelisted, and the user hasn't ever been whitelisted, whitelist them
                if (BotSettings.AutoWhitelistEnabled == true && user.Level < (int)AccessLevels.Levels.Whitelisted
                    && user.AutoWhitelisted == false && user.ValidInputs >= BotSettings.AutoWhitelistInputCount)
                {
                    user.Level = (int)AccessLevels.Levels.Whitelisted;
                    user.SetAutoWhitelist(true);
                    if (string.IsNullOrEmpty(BotSettings.AutoWhitelistMsg) == false)
                    {
                        //Replace the user's name with the message
                        string msg = BotSettings.AutoWhitelistMsg.Replace("{0}", user.Name);
                        QueueMessage(msg);
                    }
                }
            }
        }

        private void OnWhisperReceived(OnWhisperReceivedArgs e)
        {
            
        }

        private void OnBeingHosted(OnBeingHostedArgs e)
        {
            QueueMessage($"Thank you for hosting, {e.BeingHostedNotification.HostedByChannel}!!");
        }

        private void OnNewSubscriber(User user, OnNewSubscriberArgs e)
        {
            QueueMessage($"Thank you for subscribing, {e.Subscriber.DisplayName} :D !!");
        }

        private void OnReSubscriber(User user, OnReSubscriberArgs e)
        {
            QueueMessage($"Thank you for subscribing for {e.ReSubscriber.Months} months, {e.ReSubscriber.DisplayName} :D !!");
        }

        private void OnReconnected(OnReconnectedEventArgs e)
        {
            QueueMessage("Successfully reconnected to chat!");

            TryReconnect = false;
        }

        private void OnDisconnected(OnDisconnectedEventArgs e)
        {
            Console.WriteLine("Disconnected!");

            TryReconnect = true;
        }

        public static User GetUser(string username, bool isLower = true)
        {
            if (isLower == false)
            {
                username = username.ToLowerInvariant();
            }

            BotData.Users.TryGetValue(username, out User userData);

            return userData;
        }

        /// <summary>
        /// Gets a user object by username and adds the user object if the username isn't found.
        /// </summary>
        /// <param name="username">The name of the user.</param>
        /// <param name="isLower">Whether the username is all lower-case or not.
        /// If false, will make the username lowercase before checking the name.</param>
        /// <returns>A User object associated with <paramref name="username"/>.</returns>
        public static User GetOrAddUser(string username, bool isLower = true)
        {
            string origName = username;
            if (isLower == false)
            {
                username = username.ToLowerInvariant();
            }

            User userData = null;

            //Check to add a user that doesn't exist
            if (BotData.Users.TryGetValue(username, out userData) == false)
            {
                userData = new User();
                userData.Name = username;
                BotData.Users.TryAdd(username, userData);

                BotProgram.QueueMessage($"Welcome to the stream, {origName} :D ! We hope you enjoy your stay!");
            }

            return userData;
        }

        public static void SaveSettings()
        {
            //Make sure more than one thread doesn't try to save at the same time to prevent potential loss of data and access violations
            lock (SettingsLockObj)
            {
                string text = JsonConvert.SerializeObject(BotSettings, Formatting.Indented);
                if (string.IsNullOrEmpty(text) == false)
                {
                    if (Globals.SaveToTextFile(Globals.SettingsFilename, text) == false)
                    {
                        QueueMessage($"CRITICAL - Unable to save settings");
                    }
                }
            }
        }

        public static void SaveBotData()
        {
            //Make sure more than one thread doesn't try to save at the same time to prevent potential loss of data and access violations
            lock (BotDataLockObj)
            {
                string text = JsonConvert.SerializeObject(BotData, Formatting.Indented);
                if (string.IsNullOrEmpty(text) == false)
                {
                    if (Globals.SaveToTextFile(Globals.BotDataFilename, text) == false)
                    {
                        QueueMessage($"CRITICAL - Unable to save bot data");
                    }
                }
            }
        }

        public static void LoadSettingsAndBotData()
        {
            bool settingsChanged = false;
            
            string settingsText = Globals.ReadFromTextFileOrCreate(Globals.SettingsFilename);
            BotSettings = JsonConvert.DeserializeObject<Settings>(settingsText);

            if (BotSettings == null)
            {
                Console.WriteLine("No settings found; attempting to create file template. If created, please manually fill out the information.");

                BotSettings = new Settings();
                settingsChanged = true;
            }

            if (BotSettings.MainThreadSleep < Globals.MinSleepTime)
            {
                BotSettings.MainThreadSleep = Globals.MinSleepTime;
                Console.WriteLine($"Clamped sleep time to the minimum of {Globals.MinSleepTime}ms!");
                settingsChanged = true;
            }
            else if (BotSettings.MainThreadSleep > Globals.MaxSleepTime)
            {
                BotSettings.MainThreadSleep = Globals.MaxSleepTime;
                Console.WriteLine($"Clamped sleep time to the maximum of {Globals.MinSleepTime}ms!");
                settingsChanged = true;
            }
            
            //Write only once after checking all the changes
            if (settingsChanged == true)
            {
                SaveSettings();
            }

            string dataText = Globals.ReadFromTextFile(Globals.BotDataFilename);
            BotData = JsonConvert.DeserializeObject<BotData>(dataText);

            if (BotData == null)
            {
                Console.WriteLine("Not bot data found; initializing new bot data.");

                BotData = new BotData();
                SaveBotData();
            }

            //string achievementsText = Globals.ReadFromTextFileOrCreate(Globals.AchievementsFilename);
            //BotData.Achievements = JsonConvert.DeserializeObject<AchievementData>(achievementsText);
            //if (BotData.Achievements == null)
            //{
            //    Console.WriteLine("No achievement data found; initializing template.");
            //    BotData.Achievements = new AchievementData();

            //    //Add an example achievement
            //    BotData.Achievements.AchievementDict.Add("talkative", new Achievement("Talkative",
            //        "Say 500 messages in chat.", AchievementTypes.MsgCount, 500, 1000L)); 

            //    //Save the achievement template
            //    string text = JsonConvert.SerializeObject(BotData.Achievements, Formatting.Indented);
            //    if (string.IsNullOrEmpty(text) == false)
            //    {
            //        if (Globals.SaveToTextFile(Globals.AchievementsFilename, text) == false)
            //        {
            //            QueueMessage($"CRITICAL - Unable to save achievement data");
            //        }
            //    }
            //}
        }

#endregion

        private class LoginInfo
        {
            public string BotName = string.Empty;
            public string Password = string.Empty;
            public string ChannelName = string.Empty;
        }

        public class Settings
        {
            public int MessageTime = 30;
            public double MessageCooldown = 1000d;
            public double CreditsTime = 2d;
            public long CreditsAmount = 100L;
            
            /// <summary>
            /// How long to make the main thread sleep after each iteration.
            /// Higher values use less CPU at the expense of delaying queued messages and routines.
            /// </summary>
            public int MainThreadSleep = 100;

            /// <summary>
            /// The message to send when the bot connects to a channel. "{0}" is replaced with the name of the bot and "{1}" is replaced with the command identifier.
            /// </summary>
            public string ConnectMessage = "{0} has connected :D ! Use {1}help to display a list of commands and {1}tutorial to see how to play! Original input parser by Jdog, aka TwitchPlays_Everything, converted to C# & improved by the community.";

            /// <summary>
            /// If true, automatically whitelists users if conditions are met, including the command count.
            /// </summary>
            public bool AutoWhitelistEnabled = false;

            /// <summary>
            /// The number of valid inputs required to whitelist a user if they're not whitelisted and auto whitelist is enabled.
            /// </summary>
            public int AutoWhitelistInputCount = 20;

            /// <summary>
            /// The message to send when a user is auto whitelisted. "{0}" is replaced with the name of the user whitelisted.
            /// </summary>
            public string AutoWhitelistMsg = "{0} has been whitelisted! New commands are available.";
            
            /// <summary>
            /// If true, will acknowledge that a chat bot is in use and allow interacting with it, provided it's set up.
            /// </summary>
            public bool UseChatBot = false;
        }
    }
}
