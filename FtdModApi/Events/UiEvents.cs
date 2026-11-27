using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using BrilliantSkies.Ftd.Avatar.Interact;
using BrilliantSkies.Ui.Displayer;
using BrilliantSkies.Ui.Displayer.Internal;
using BrilliantSkies.Ui.Displayer.Types;
using FtdModApi.Patching;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>
        /// The game's windows (block menus, the map, build menus, settings...) opening and closing, and the local player
        /// interacting with a block (Q). All local: they happen on the machine of the player who opened them.
        /// </summary>
        public static class Ui
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook OpenHook = new EventHook(Features.EventUi, () => new[]
            {
                new PatchSpec
                {
                    Id = "GuiDisplayBase.ActivateUi: Ui.WindowOpening",
                    FeatureId = Features.EventUi,
                    Target = () => EventHelpers.Declared(typeof(GuiDisplayBase), "BrilliantSkies.Ui.Displayer.IGuiDisplayerInternal.ActivateUi"),
                    Prefix = PatchSpec.Hook(typeof(Ui), nameof(ActivateUi_Prefix))
                },
                new PatchSpec
                {
                    Id = "GuiStore.AddRequest: Ui.WindowOpened",
                    FeatureId = Features.EventUi,
                    Target = () => EventHelpers.Declared(typeof(GuiStore), "AddRequest"),
                    Prefix = PatchSpec.Hook(typeof(Ui), nameof(AddRequest_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Ui), nameof(AddRequest_Finalizer))
                },
                new PatchSpec
                {
                    // Bringing an open window back to the top re-adds it; that is not an opening.
                    Id = "GuiStore.BringToTop: Ui.WindowOpened (skip re-adds)",
                    FeatureId = Features.EventUi,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(GuiStore), "BringToTop"),
                    Prefix = PatchSpec.Hook(typeof(Ui), nameof(BringToTop_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Ui), nameof(BringToTop_Finalizer))
                }
            });

            private static readonly EventHook CloseHook = new EventHook(Features.EventUi, () => new[]
            {
                ClosePatch("PopAll"),
                ClosePatch("PopTop"),
                ClosePatch("PopSelected"),
                new PatchSpec
                {
                    // Called on every GUI event: only look closer when it actually removed something.
                    Id = "GuiStore.PopInvalidSubject: Ui.WindowClosed",
                    FeatureId = Features.EventUi,
                    Target = () => EventHelpers.Declared(typeof(GuiStore), "PopInvalidSubject"),
                    Prefix = PatchSpec.Hook(typeof(Ui), nameof(PopInvalid_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Ui), nameof(PopInvalid_Finalizer))
                }
            });

            private static PatchSpec ClosePatch(string method) => new PatchSpec
            {
                Id = "GuiStore." + method + ": Ui.WindowClosed",
                FeatureId = Features.EventUi,
                Target = () => EventHelpers.Declared(typeof(GuiStore), method),
                Prefix = PatchSpec.Hook(typeof(Ui), nameof(Pop_Prefix)),
                Finalizer = PatchSpec.Hook(typeof(Ui), nameof(Pop_Finalizer))
            };

            private static readonly EventHook InteractHook = new EventHook(Features.EventUi, () => new[]
            {
                new PatchSpec
                {
                    Id = "cInteract.PerformSecondary: Ui.BlockInteracting/BlockInteracted",
                    FeatureId = Features.EventUi,
                    Target = () => EventHelpers.Declared(typeof(cInteract), "PerformSecondary"),
                    Prefix = PatchSpec.Hook(typeof(Ui), nameof(PerformSecondary_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Ui), nameof(PerformSecondary_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// A window is about to open (also when an open window is opened again: the game closes and re-adds it).
            /// Cancelling keeps it closed; use with care, the game may expect some windows to open, and a toggle button
            /// still believes it opened. Raised even when the game then decides not to open it. Focus may not be set
            /// yet. A window switching to another subject (same window, new block) raises nothing. Local.
            /// </summary>
            public static EventChannel<WindowOpeningEvent> WindowOpening { get; } = new EventChannel<WindowOpeningEvent>(
                "Ui.WindowOpening", EventSide.Local, "A window is about to open.", OpenHook);

            /// <summary>
            /// A window opened (is now on screen). When a window opens others along with it (the map's AI button opens
            /// one AI window per mainframe, up to four, and each can open a routine editor), the first window is reported
            /// first. Local.
            /// </summary>
            public static EventChannel<WindowEvent> WindowOpened { get; } = new EventChannel<WindowEvent>(
                "Ui.WindowOpened", EventSide.Local, "A window opened.", OpenHook);

            /// <summary>A window closed (closed by the player, replaced by another, or its subject went away). Local.</summary>
            public static EventChannel<WindowEvent> WindowClosed { get; } = new EventChannel<WindowEvent>(
                "Ui.WindowClosed", EventSide.Local, "A window closed.", CloseHook);

            /// <summary>
            /// The local player is about to interact with a block (Q on it, usually opening its menu). Cancelling stops
            /// the interaction. Local.
            /// </summary>
            public static EventChannel<BlockInteractingEvent> BlockInteracting { get; } = new EventChannel<BlockInteractingEvent>(
                "Ui.BlockInteracting", EventSide.Local, "The player is about to interact with a block.", InteractHook);

            /// <summary>The local player interacted with a block. Local.</summary>
            public static EventChannel<BlockInteractedEvent> BlockInteracted { get; } = new EventChannel<BlockInteractedEvent>(
                "Ui.BlockInteracted", EventSide.Local, "The player interacted with a block.", InteractHook);

            // ---- opening -----------------------------------------------------------------------------------

            // The window being brought back to the top (it was already open), so its re-add is not reported.
            [ThreadStatic] private static IGui_GuiSystem _bringingToTop;

            private static void BringToTop_Prefix(GuiStore __instance, IGui_GuiSystem toBringToTop, out IGui_GuiSystem __state)
            {
                __state = _bringingToTop;
                try
                {
                    foreach (var gui in __instance.AllUis)
                        if (ReferenceEquals(gui, toBringToTop))
                        {
                            _bringingToTop = toBringToTop;
                            break;
                        }
                }
                catch
                {
                }
            }

            private static void BringToTop_Finalizer(IGui_GuiSystem __state)
            {
                _bringingToTop = __state;
            }

            private static bool ActivateUi_Prefix(IGui_GuiSystem gui, GuiActivateType type, ref bool __result)
            {
                if (gui == null || !WindowOpening.HasSubscribers) return true;
                var e = new WindowOpeningEvent(gui, type);
                WindowOpening.Raise(e);
                if (!e.IsCancelled) return true;
                __result = false;
                return false;
            }

            // A window often opens its own child windows while it opens (the AI window opens the routine editor).
            // Openings are queued in the order they started and raised when the outermost one finishes, so a
            // parent is reported before its children, and only windows that are still open are reported.
            [ThreadStatic] private static int _addDepth;
            [ThreadStatic] private static List<IGui_GuiSystem> _opening;

            private static void AddRequest_Prefix(UiStackLevel newRequest)
            {
                _addDepth++;
                if (newRequest == null || !WindowOpened.HasSubscribers) return;
                IGui_GuiSystem gui;
                try
                {
                    gui = newRequest.Uis != null && newRequest.Uis.Count > 0 ? newRequest.Uis[0] : null;
                }
                catch
                {
                    return;
                }
                if (gui == null || ReferenceEquals(gui, _bringingToTop)) return;
                (_opening ?? (_opening = new List<IGui_GuiSystem>())).Add(gui);
            }

            private static void AddRequest_Finalizer(GuiStore __instance)
            {
                if (_addDepth > 0) _addDepth--;
                if (_addDepth > 0 || _opening == null || _opening.Count == 0) return;
                var opened = _opening.ToArray();
                _opening.Clear();
                HashSet<IGui_GuiSystem> open;
                try
                {
                    open = new HashSet<IGui_GuiSystem>(__instance.AllUis);
                }
                catch
                {
                    return;
                }
                foreach (var gui in opened)
                {
                    if (open.Contains(gui)) WindowOpened.Raise(new WindowEvent(gui, FocusOf(gui)));
                }
            }

            // ---- closing -----------------------------------------------------------------------------------

            // Closing a window can close others from inside (a menu closing its popup), so only the outermost Pop
            // compares before and after; each window is reported once, with what it showed before it closed.
            [ThreadStatic] private static int _popDepth;
            [ThreadStatic] private static List<KeyValuePair<IGui_GuiSystem, object>> _popBefore;
            [ThreadStatic] private static int _popCountBefore;

            private static void Pop_Prefix(GuiStore __instance)
            {
                if (_popDepth++ > 0) return;
                _popBefore = null;
                if (__instance == null || !WindowClosed.HasSubscribers) return;
                try
                {
                    _popCountBefore = __instance.AllUiCount;
                    if (_popCountBefore == 0) return;
                    var list = new List<KeyValuePair<IGui_GuiSystem, object>>(_popCountBefore);
                    foreach (var gui in __instance.AllUis) list.Add(new KeyValuePair<IGui_GuiSystem, object>(gui, FocusOf(gui)));
                    _popBefore = list;
                }
                catch
                {
                    _popBefore = null;
                }
            }

            private static void Pop_Finalizer(GuiStore __instance)
            {
                if (_popDepth > 0) _popDepth--;
                if (_popDepth > 0) return;
                var before = _popBefore;
                _popBefore = null;
                if (before == null || __instance == null) return;
                try
                {
                    // The game calls PopInvalidSubject on every GUI event; usually nothing closed.
                    if (__instance.AllUiCount == _popCountBefore && _popCountBefore > 0 && StillAllOpen(__instance, before)) return;
                    var still = new HashSet<IGui_GuiSystem>(__instance.AllUis);
                    foreach (var kv in before)
                    {
                        if (kv.Key == null || still.Contains(kv.Key)) continue;
                        // Opened and closed again while it was opening: report neither.
                        if (_opening != null && _opening.Remove(kv.Key)) continue;
                        WindowClosed.Raise(new WindowEvent(kv.Key, kv.Value));
                    }
                }
                catch
                {
                }
            }

            // PopInvalidSubject removes windows whose subject is gone; the window has already let go of it by then.
            private static void PopInvalid_Prefix(GuiStore __instance, out int __state)
            {
                __state = -1;
                if (_popDepth > 0 || __instance == null || !WindowClosed.HasSubscribers) return;
                try
                {
                    __state = __instance.AllUiCount;
                    if (__state == 0) return;
                    // Cheap check first: does any window have an invalid subject? Only then take a snapshot.
                    foreach (var gui in __instance.AllUis)
                    {
                        if (!gui.StillHasAValidSubject())
                        {
                            Pop_Prefix(__instance);
                            __state = -2; // snapshot taken
                            return;
                        }
                    }
                }
                catch
                {
                    __state = -1;
                }
            }

            private static void PopInvalid_Finalizer(GuiStore __instance, int __state)
            {
                if (__state == -2) Pop_Finalizer(__instance);
            }

            private static bool StillAllOpen(GuiStore store, List<KeyValuePair<IGui_GuiSystem, object>> before)
            {
                foreach (var gui in store.AllUis)
                {
                    bool found = false;
                    for (int i = 0; i < before.Count; i++)
                    {
                        if (ReferenceEquals(before[i].Key, gui))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found) return false;
                }
                return true;
            }

            // ---- block interaction -------------------------------------------------------------------------

            private static bool PerformSecondary_Prefix(cInteract __instance, ref bool __result, out Block __state)
            {
                __state = null;
                if (__instance == null) return true;
                Block block;
                try
                {
                    block = __instance.InteractWith;
                }
                catch
                {
                    return true;
                }
                if (block == null) return true;
                __state = block;
                if (!BlockInteracting.HasSubscribers) return true;
                var e = new BlockInteractingEvent(block);
                BlockInteracting.Raise(e);
                if (!e.IsCancelled) return true;
                __state = null;
                __result = false;
                return false;
            }

            private static void PerformSecondary_Postfix(bool __result, Block __state)
            {
                if (!__result || __state == null || !BlockInteracted.HasSubscribers) return;
                BlockInteracted.Raise(new BlockInteractedEvent(__state));
            }

            // ---- window details ----------------------------------------------------------------------------

            private static readonly ConcurrentDictionary<Type, FieldInfo> _focusFields = new ConcurrentDictionary<Type, FieldInfo>();

            /// <summary>The object a window is about (the block, craft... it shows), read from the game's _focus field.</summary>
            internal static object FocusOf(IGui_GuiSystem gui)
            {
                if (gui == null) return null;
                try
                {
                    var field = _focusFields.GetOrAdd(gui.GetType(), t =>
                    {
                        for (var type = t; type != null && type != typeof(object); type = type.BaseType)
                        {
                            var f = type.GetField("_focus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (f != null) return f;
                        }
                        return null;
                    });
                    return field?.GetValue(gui);
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>A window opened or closed.</summary>
    public class WindowEvent : GameEvent
    {
        internal WindowEvent(IGui_GuiSystem window, object focus)
        {
            Window = window;
            TypeName = window?.GetType().Name;
            Focus = focus;
        }

        /// <summary>The window. Game object.</summary>
        public IGui_GuiSystem Window { get; }

        /// <summary>The window's class name, e.g. "AiControlUi" (stable across languages; use it to recognise windows).</summary>
        public string TypeName { get; }

        /// <summary>
        /// What the window is about (a block, a craft, a fleet...), or null. For WindowClosed, what it showed before it
        /// closed. Some windows set it only after opening, so it can be null in WindowOpened. Game object.
        /// </summary>
        public object Focus { get; }

        /// <summary>The block the window is about, or null.</summary>
        public Block Block => Focus as Block;

        /// <summary>True when the window is of this type (or derives from it).</summary>
        public bool Is<T>() where T : class => Window is T;
    }

    /// <summary>A window is about to open. Cancellable.</summary>
    public sealed class WindowOpeningEvent : CancellableEvent
    {
        internal WindowOpeningEvent(IGui_GuiSystem window, GuiActivateType type)
        {
            Window = window;
            ActivateType = type;
            TypeName = window?.GetType().Name;
            Focus = Events.Ui.FocusOf(window);
        }

        public IGui_GuiSystem Window { get; }

        public string TypeName { get; }

        /// <summary>How it is being opened (replacing other windows, stacked on top, ...).</summary>
        public GuiActivateType ActivateType { get; }

        public object Focus { get; }

        public Block Block => Focus as Block;

        public bool Is<T>() where T : class => Window is T;
    }

    /// <summary>The local player is about to interact with a block. Cancellable.</summary>
    public sealed class BlockInteractingEvent : CancellableEvent
    {
        internal BlockInteractingEvent(Block block)
        {
            Block = block;
        }

        /// <summary>The block. Game object.</summary>
        public Block Block { get; }

        public string ItemName => EventHelpers.ItemName(Block?.item);

        /// <summary>The craft (or turret) it is on.</summary>
        public AllConstruct Construct => EventHelpers.ConstructOf(Block);
    }

    /// <summary>The local player interacted with a block.</summary>
    public sealed class BlockInteractedEvent : GameEvent
    {
        internal BlockInteractedEvent(Block block)
        {
            Block = block;
        }

        public Block Block { get; }

        public string ItemName => EventHelpers.ItemName(Block?.item);

        public AllConstruct Construct => EventHelpers.ConstructOf(Block);
    }
}
