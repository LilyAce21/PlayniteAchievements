using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// Decides when each SAN unlock notification may start: how many can share the screen, which
    /// rarity tiers may appear while another notification is still visible, and that a capstone or
    /// 100% completion always follows the achievement that triggered it (capstone first). Everything
    /// here runs on the UI thread.
    /// </summary>
    internal sealed class SanNotificationScheduler
    {
        internal enum TicketKind
        {
            Normal = 0,
            Capstone = 1,
            Completion = 2
        }

        internal struct Policy
        {
            public int MaxStacked;
            public bool StackCommon;
            public bool StackUncommon;
            public bool StackRare;
            public bool StackUltraRare;
            public bool StackCapstone;
            public bool StackCompletion;
            public bool SkipCompletionWithCapstone;

            public bool MayStack(string profileKey)
            {
                switch ((profileKey ?? string.Empty).ToLowerInvariant())
                {
                    case "common": return StackCommon;
                    case "uncommon": return StackUncommon;
                    case "rare": return StackRare;
                    case "ultrarare": return StackUltraRare;
                    case "capstone": return StackCapstone;
                    case "completion": return StackCompletion;
                    default: return false;
                }
            }
        }

        internal sealed class Ticket
        {
            public string Game;
            public string Label;
            public string ProfileKey;
            public TicketKind Kind;
            /// <summary>Shows the notification. Calls the supplied action when its screen slot is free again.</summary>
            public Func<Action, bool> Start;
            internal bool CanStack;
            internal bool Released;
            internal DispatcherTimer Watchdog;
        }

        private static readonly TimeSpan SimultaneousCapstoneWindow = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SlotWatchdog = TimeSpan.FromSeconds(90);

        private readonly List<Ticket> _onScreen = new List<Ticket>();
        private readonly List<Ticket> _queue = new List<Ticket>();
        private readonly Dictionary<string, DateTime> _recentCapstone =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private Func<Policy> _policy;
        private Action<string> _log;
        private bool _pumping;

        public void Enqueue(Ticket ticket, Func<Policy> policy, Action<string> log)
        {
            if (ticket == null || ticket.Start == null)
            {
                return;
            }

            _policy = policy;
            _log = log;
            var current = policy();
            var game = ticket.Game ?? string.Empty;

            if (ticket.Kind == TicketKind.Completion && current.SkipCompletionWithCapstone && CapstoneAlreadyHandled(game))
            {
                _log?.Invoke($"Skipping the 100% notification '{ticket.Label}': the capstone for this game fired together with it.");
                return;
            }

            if (ticket.Kind == TicketKind.Capstone)
            {
                _recentCapstone[game] = DateTime.UtcNow;
                if (current.SkipCompletionWithCapstone)
                {
                    var dropped = _queue.RemoveAll(t => t.Kind == TicketKind.Completion &&
                        string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase));
                    if (dropped > 0)
                    {
                        _log?.Invoke($"Dropped {dropped} queued 100% notification(s): the capstone fires together with it.");
                    }
                }
            }

            ticket.CanStack = ticket.Kind == TicketKind.Normal
                ? current.MayStack(ticket.ProfileKey)
                : current.MayStack(ticket.ProfileKey);

            // Within one game the order is always: the achievement, then its capstone, then 100%.
            var index = _queue.FindIndex(t =>
                string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase) && t.Kind > ticket.Kind);
            if (index < 0)
            {
                _queue.Add(ticket);
            }
            else
            {
                _queue.Insert(index, ticket);
            }

            Pump();
        }

        private bool CapstoneAlreadyHandled(string game)
        {
            if (_queue.Any(t => t.Kind == TicketKind.Capstone && string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase)) ||
                _onScreen.Any(t => t.Kind == TicketKind.Capstone && string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return _recentCapstone.TryGetValue(game, out var when) &&
                   DateTime.UtcNow - when <= SimultaneousCapstoneWindow;
        }

        private bool CanStartNow(Ticket head, Policy policy)
        {
            if (_onScreen.Count == 0)
            {
                return true;
            }

            // A capstone or 100% notification never starts while something of the same game that
            // should come before it is still visible.
            if (head.Kind != TicketKind.Normal &&
                _onScreen.Any(t => t.Kind < head.Kind &&
                                   string.Equals(t.Game, head.Game, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!head.CanStack || _onScreen.Any(t => !t.CanStack))
            {
                return false;
            }

            return _onScreen.Count < Math.Max(1, Math.Min(3, policy.MaxStacked));
        }

        private void Pump()
        {
            if (_pumping)
            {
                return;
            }

            _pumping = true;
            try
            {
                while (_queue.Count > 0)
                {
                    var head = _queue[0];
                    if (!CanStartNow(head, _policy()))
                    {
                        break;
                    }

                    _queue.RemoveAt(0);
                    StartTicket(head);
                }
            }
            finally
            {
                _pumping = false;
            }
        }

        private void StartTicket(Ticket ticket)
        {
            _onScreen.Add(ticket);
            var shown = false;
            try
            {
                shown = ticket.Start(() => Release(ticket));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Could not start '{ticket.Label}': {ex.Message}");
            }

            if (!shown)
            {
                Release(ticket);
                return;
            }

            if (!ticket.Released)
            {
                try
                {
                    ticket.Watchdog = new DispatcherTimer { Interval = SlotWatchdog };
                    ticket.Watchdog.Tick += (_, __) => Release(ticket);
                    ticket.Watchdog.Start();
                }
                catch
                {
                }
            }
        }

        private void Release(Ticket ticket)
        {
            if (ticket == null || ticket.Released)
            {
                return;
            }

            ticket.Released = true;
            try { ticket.Watchdog?.Stop(); } catch { }
            _onScreen.Remove(ticket);
            Pump();
        }
    }
}
