// =============================================================================
//  Stateless.Slim - a single-file, synchronous ("coroutine-free") edition of
//  the Stateless state machine library.
//
//  Upstream: https://github.com/dotnet-state-machine/stateless  (Apache-2.0)
//
//  Removed compared to the standard Stateless 5.x sources
//  ---------------------------------------------------------------------------
//    * All async/await ("coroutine") support: Fire/Activate/Deactivate,
//      PermittedTriggers, entry/exit/activate/deactivate actions, guards and
//      unhandled-trigger handlers are synchronous only. No System.Threading.Tasks
//      dependency and no task allocation per transition, which keeps it friendly
//      to Unity frame loops, IL2CPP and AOT builds.
//    * Graph export (Stateless.Graph: StateGraph/UmlDotGraph/MermaidGraph).
//    * The reflection/introspection API (Stateless.Reflection, GetInfo()).
//    * .resx resources: exception messages are inlined, so there is no satellite
//      assembly and no runtime resource lookup.
//
//  Kept (the complete synchronous public API)
//  ---------------------------------------------------------------------------
//    Permit / PermitIf / PermitReentry / PermitReentryIf / PermitDynamic /
//    PermitDynamicIf, Ignore / IgnoreIf, InternalTransition /
//    InternalTransitionIf, SubstateOf, InitialTransition, OnEntry / OnEntryFrom /
//    OnExit / OnActivate / OnDeactivate, Fire (plus the typed overloads for up to
//    three trigger parameters), CanFire (plus unmet guard descriptions),
//    PermittedTriggers / GetPermittedTriggers / GetDetailedPermittedTriggers,
//    IsInState, OnTransitioned / OnTransitionCompleted (+ unregister variants),
//    OnUnhandledTrigger, FiringMode.Immediate / FiringMode.Queued,
//    Activate / Deactivate.
//
//  Usage in Unity
//  ---------------------------------------------------------------------------
//    Drop this single file anywhere under Assets/ (for example
//    Assets/Plugins/Stateless/Stateless.Slim.cs). No package, no asmdef and no
//    other file is needed. Language level: C# 7.3 / .NET Standard 2.0, so it
//    compiles from Unity 2018.4 up to the newest versions, Mono and IL2CPP.
//
//      using Stateless;
//
//      enum State { Off, On }
//      enum Trigger { Toggle }
//
//      var machine = new StateMachine<State, Trigger>(State.Off);
//      machine.Configure(State.Off).Permit(Trigger.Toggle, State.On);
//      machine.Configure(State.On).Permit(Trigger.Toggle, State.Off);
//
//      machine.Fire(Trigger.Toggle);       // machine.State == State.On
//      bool canFire = machine.CanFire(Trigger.Toggle);
//
//  API differences that follow from the removals
//  ---------------------------------------------------------------------------
//    * GetDetailedPermittedTriggers() is synchronous here (it used to return a
//      Task<IEnumerable<TriggerDetails<..>>>).
//    * Description arguments (entryActionDescription, guardDescription,
//      destinationStateSelectorDescription, ...) are still accepted so existing
//      call sites keep compiling; guard descriptions are what unmet-guard
//      reporting uses, the action/selector descriptions are no longer stored
//      because only the introspection API consumed them.
//    * PermitDynamic/PermitDynamicIf no longer take the optional
//      DynamicStateInfos possibleDestinationStates argument (introspection type).
//    * RetainSynchronizationContext was removed: it only controlled the
//      SynchronizationContext of await continuations.
//
//  Licensed under the Apache License, Version 2.0.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace Stateless
{
    /// <summary>
    /// Enum for the different modes used when <c>Fire</c>ing a trigger
    /// </summary>
    public enum FiringMode
    {
        /// <summary> Use immediate mode when the queuing of trigger events are not needed. Care must be taken when using this mode, as there is no run-to-completion guaranteed.</summary>
        Immediate,
        /// <summary> Use the queued <c>Fire</c>ing mode when run-to-completion is required. This is the recommended mode.</summary>
        Queued
    }

    /// <summary>
    /// Represents a trigger with details of any configured trigger parameters.
    /// </summary>
    public sealed class TriggerDetails<TState, TTrigger>
    {
        /// <summary>
        /// Creates a new instance of <see cref="TriggerDetails{TState, TTrigger}"/>.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        /// <param name="triggerConfiguration">The trigger configurations dictionary.</param>
        internal TriggerDetails(TTrigger trigger, IDictionary<TTrigger, StateMachine<TState, TTrigger>.TriggerWithParameters> triggerConfiguration)
        {
            Trigger = trigger;
            HasParameters = triggerConfiguration.ContainsKey(trigger);
            Parameters = HasParameters ? triggerConfiguration[trigger] : null;
        }

        /// <summary>
        /// Gets the trigger.
        /// </summary>
        public TTrigger Trigger { get; }

        /// <summary>
        /// Gets a value indicating whether the trigger has been configured with parameters.
        /// </summary>
        public bool HasParameters { get; }

        /// <summary>
        /// When <see cref="HasParameters"/> is <code>true</code>, returns the parameters required by 
        /// this trigger; otherwise, returns <code>null</code>.
        /// </summary>
        public StateMachine<TState, TTrigger>.TriggerWithParameters Parameters { get; }
    }

    /// <summary>
    /// Models behaviour as transitions between a finite set of states.
    /// </summary>
    /// <typeparam name="TState">The type used to represent the states.</typeparam>
    /// <typeparam name="TTrigger">The type used to represent the triggers that cause state transitions.</typeparam>
    public partial class StateMachine<TState, TTrigger>
    {
        #region Core state machine
        private readonly IDictionary<TState, StateRepresentation> _stateConfiguration = new Dictionary<TState, StateRepresentation>();
        private readonly IDictionary<TTrigger, TriggerWithParameters> _triggerConfiguration = new Dictionary<TTrigger, TriggerWithParameters>();
        private readonly Func<TState> _stateAccessor;
        private readonly Action<TState> _stateMutator;
        private Action<TState, TTrigger, ICollection<string>> _unhandledTriggerAction;
        private readonly OnTransitionedEvent _onTransitionedEvent;
        private readonly OnTransitionedEvent _onTransitionCompletedEvent;
        private readonly TState _initialState;
        private readonly FiringMode _firingMode;

        private class QueuedTrigger
        {
            public TTrigger Trigger { get; set; }
            public object[] Args { get; set; }
        }

        private readonly Queue<QueuedTrigger> _eventQueue = new Queue<QueuedTrigger>();
        private bool _firing;

        /// <summary>
        /// Construct a state machine with external state storage.
        /// </summary>
        /// <param name="stateAccessor">A function that will be called to read the current state value.</param>
        /// <param name="stateMutator">An action that will be called to write new state values.</param>
        public StateMachine(Func<TState> stateAccessor, Action<TState> stateMutator) : this(stateAccessor, stateMutator, FiringMode.Queued)
        {
        }

        /// <summary>
        /// Construct a state machine.
        /// </summary>
        /// <param name="initialState">The initial state.</param>
        public StateMachine(TState initialState) : this(initialState, FiringMode.Queued)
        {
        }

        /// <summary>
        /// Construct a state machine with external state storage.
        /// </summary>
        /// <param name="stateAccessor">A function that will be called to read the current state value.</param>
        /// <param name="stateMutator">An action that will be called to write new state values.</param>
        /// <param name="firingMode">Optional specification of firing mode.</param>
        public StateMachine(Func<TState> stateAccessor, Action<TState> stateMutator, FiringMode firingMode) : this()
        {
            _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
            _stateMutator = stateMutator ?? throw new ArgumentNullException(nameof(stateMutator));

            _initialState = stateAccessor();
            _firingMode = firingMode;
        }

        /// <summary>
        /// Construct a state machine.
        /// </summary>
        /// <param name="initialState">The initial state.</param>
        /// <param name="firingMode">Optional specification of firing mode.</param>
        public StateMachine(TState initialState, FiringMode firingMode) : this()
        {
            var reference = new StateReference { State = initialState };
            _stateAccessor = () => reference.State;
            _stateMutator = s => reference.State = s;

            _initialState = initialState;
            _firingMode = firingMode;
        }

/// <summary>
        /// Default constructor
        /// </summary>
        StateMachine()
        {
            _unhandledTriggerAction = DefaultUnhandledTriggerAction;
            _onTransitionedEvent = new OnTransitionedEvent();
            _onTransitionCompletedEvent = new OnTransitionedEvent();
        }

        /// <summary>
        /// The current state.
        /// </summary>
        public TState State
        {
            get
            {
                return _stateAccessor();
            }
            private set
            {
                _stateMutator(value);
            }
        }

        /// <summary>
        /// The currently-permissible trigger values.
        /// </summary>
        public IEnumerable<TTrigger> PermittedTriggers => GetPermittedTriggers();

        /// <summary>
        /// Gets the currently-permissible trigger values.
        /// </summary>
        public IEnumerable<TTrigger> GetPermittedTriggers(params object[] args)
        {
            return CurrentRepresentation.GetPermittedTriggers(args);
        }

        /// <summary>
        /// Gets the currently-permissible triggers with any configured parameters.
        /// </summary>
        public IEnumerable<TriggerDetails<TState, TTrigger>> GetDetailedPermittedTriggers(params object[] args)
        {
            return CurrentRepresentation.GetPermittedTriggers(args)
                .Select(trigger => new TriggerDetails<TState, TTrigger>(trigger, _triggerConfiguration));
        }

        StateRepresentation CurrentRepresentation
        {
            get
            {
                return GetRepresentation(State);
            }
        }

StateRepresentation GetRepresentation(TState state)
        {
            if (!_stateConfiguration.TryGetValue(state, out StateRepresentation result))
            {
                result = new StateRepresentation(state);
                _stateConfiguration.Add(state, result);
            }

            return result;
        }

        /// <summary>
        /// Begin configuration of the entry/exit actions and allowed transitions
        /// when the state machine is in a particular state.
        /// </summary>
        /// <param name="state">The state to configure.</param>
        /// <returns>A configuration object through which the state can be configured.</returns>
        public StateConfiguration Configure(TState state)
        {
            return new StateConfiguration(this, GetRepresentation(state), GetRepresentation);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <param name="trigger">The trigger to fire.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public void Fire(TTrigger trigger)
        {
            InternalFire(trigger, new object[0]);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <param name="trigger">The trigger to fire.</param>
        /// <param name="args">A variable-length parameters list containing arguments. </param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public void Fire(TriggerWithParameters trigger, params object[] args)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));
            InternalFire(trigger.Trigger, args);
        }

        /// <summary>
        /// Specify the arguments that must be supplied when a specific trigger is fired.
        /// </summary>
        /// <param name="trigger">The underlying trigger value.</param>
        /// <param name="argumentTypes">The argument types expected by the trigger.</param>
        /// <returns>An object that can be passed to the Fire() method in order to
        /// fire the parameterised trigger.</returns>
        public TriggerWithParameters SetTriggerParameters(TTrigger trigger, params Type[] argumentTypes)
        {
            var configuration = new TriggerWithParameters(trigger, argumentTypes);
            SaveTriggerConfiguration(configuration);
            return configuration;
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <param name="trigger">The trigger to fire.</param>
        /// <param name="arg0">The first argument.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public void Fire<TArg0>(TriggerWithParameters<TArg0> trigger, TArg0 arg0)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));
            InternalFire(trigger.Trigger, arg0);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="trigger">The trigger to fire.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public void Fire<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, TArg0 arg0, TArg1 arg1)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));
            InternalFire(trigger.Trigger, arg0, arg1);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="arg2">The third argument.</param>
        /// <param name="trigger">The trigger to fire.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public void Fire<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, TArg0 arg0, TArg1 arg1, TArg2 arg2)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));
            InternalFire(trigger.Trigger, arg0, arg1, arg2);
        }

        /// <summary>
        /// Activates current state. Actions associated with activating the current state
        /// will be invoked. The activation is idempotent and subsequent activation of the same current state
        /// will not lead to re-execution of activation callbacks.
        /// </summary>
        public void Activate()
        {
            var representativeState = GetRepresentation(State);
            representativeState.Activate();
        }

        /// <summary>
        /// Deactivates current state. Actions associated with deactivating the current state
        /// will be invoked. The deactivation is idempotent and subsequent deactivation of the same current state
        /// will not lead to re-execution of deactivation callbacks.
        /// </summary>
        public void Deactivate()
        {
            var representativeState = GetRepresentation(State);
            representativeState.Deactivate();
        }

        /// <summary>
        /// Determine how to Fire the trigger
        /// </summary>
        /// <param name="trigger">The trigger. </param>
        /// <param name="args">A variable-length parameters list containing arguments. </param>
        void InternalFire(TTrigger trigger, params object[] args)
        {
            switch (_firingMode)
            {
                case FiringMode.Immediate:
                    InternalFireOne(trigger, args);
                    break;
                case FiringMode.Queued:
                    InternalFireQueued(trigger, args);
                    break;
                default:
                    // If something is completely messed up we let the user know ;-)
                    throw new InvalidOperationException("The firing mode has not been configured!");
            }
        }

        /// <summary>
        /// Queue events and then fire in order.
        /// If only one event is queued, this behaves identically to the non-queued version.
        /// </summary>
        /// <param name="trigger">  The trigger. </param>
        /// <param name="args">     A variable-length parameters list containing arguments. </param>
        private void InternalFireQueued(TTrigger trigger, params object[] args)
        {
            // Add trigger to queue
            _eventQueue.Enqueue(new QueuedTrigger { Trigger = trigger, Args = args });

            // If a trigger is already being handled then the trigger will be queued (FIFO) and processed later.
            if (_firing)
            {
                return;
            }

            try
            {
                _firing = true;

                // Empty queue for triggers
                while (_eventQueue.Any())
                {
                    var queuedEvent = _eventQueue.Dequeue();
                    InternalFireOne(queuedEvent.Trigger, queuedEvent.Args);
                }
            }
            finally
            {
                _firing = false;
            }
        }

        /// <summary>
        /// This method handles the execution of a trigger handler. It finds a
        /// handle, then updates the current state information.
        /// </summary>
        /// <param name="trigger"></param>
        /// <param name="args"></param>
        private void InternalFireOne(TTrigger trigger, params object[] args)
        {
            // If this is a trigger with parameters, we must validate the parameter(s)
            if (_triggerConfiguration.TryGetValue(trigger, out TriggerWithParameters configuration))
            {
                configuration.ValidateParameters(args);
            }

            var source = State;
            var representativeState = GetRepresentation(source);

            // Try to find a trigger handler, either in the current state or a super state.
            if (!representativeState.TryFindHandler(trigger, args, out TriggerBehaviourResult result))
            {
                _unhandledTriggerAction(representativeState.UnderlyingState, trigger, result?.UnmetGuardConditions);
                return;
            }

            switch (result.Handler)
            {
                // Check if this trigger should be ignored
                case IgnoredTriggerBehaviour _:
                    return;
                // Handle special case, re-entry in superstate
                // Check if it is an internal transition, or a transition from one state to another.
                case ReentryTriggerBehaviour handler:
                    {
                        // Handle transition, and set new state
                        var transition = new Transition(source, handler.Destination, trigger, args);
                        HandleReentryTrigger(args, representativeState, transition);
                        break;
                    }

                case DynamicTriggerBehaviour handler:
                    {
                        handler.GetDestinationState(source, args, out var destination);
                        // Handle transition, and set new state; reentry is permitted from dynamic trigger behaviours.
                        var transition = new Transition(source, destination, trigger, args);
                        HandleTransitioningTrigger(args, representativeState, transition);

                        break;
                    }
                case TransitioningTriggerBehaviour handler:
                    {
                        // If a trigger was found on a superstate that would cause unintended reentry, don't trigger.
                        if (source.Equals(handler.Destination))
                            break;

                        // Handle transition, and set new state
                        var transition = new Transition(source, handler.Destination, trigger, args);
                        HandleTransitioningTrigger(args, representativeState, transition);

                        break;
                    }
                case InternalTriggerBehaviour _:
                    {
                        // Internal transitions does not update the current state, but must execute the associated action.
                        var transition = new Transition(source, source, trigger, args);
                        CurrentRepresentation.InternalAction(transition, args);
                        break;
                    }
                default:
                    throw new InvalidOperationException("State machine configuration incorrect, no handler for trigger.");
            }
        }

        private void HandleReentryTrigger(object[] args, StateRepresentation representativeState, Transition transition)
        {
            StateRepresentation representation;
            transition = representativeState.Exit(transition);
            var newRepresentation = GetRepresentation(transition.Destination);

            if (!transition.Source.Equals(transition.Destination))
            {
                // Then Exit the final superstate
                transition = new Transition(transition.Destination, transition.Destination, transition.Trigger, args);
                newRepresentation.Exit(transition);

                _onTransitionedEvent.Invoke(transition);
                representation = EnterState(newRepresentation, transition, args);
                _onTransitionCompletedEvent.Invoke(transition);

            }
            else
            {
                _onTransitionedEvent.Invoke(transition);
                representation = EnterState(newRepresentation, transition, args);
                _onTransitionCompletedEvent.Invoke(transition);
            }
            State = representation.UnderlyingState;
        }

        private void HandleTransitioningTrigger(object[] args, StateRepresentation representativeState, Transition transition)
        {
            transition = representativeState.Exit(transition);

            State = transition.Destination;
            var newRepresentation = GetRepresentation(transition.Destination);

            //Alert all listeners of state transition
            _onTransitionedEvent.Invoke(transition);
            var representation = EnterState(newRepresentation, transition, args);

            // Check if state has changed by entering new state (by firing triggers in OnEntry or such)
            if (!representation.UnderlyingState.Equals(State))
            {
                // The state has been changed after entering the state, must update current state to new one
                State = representation.UnderlyingState;
            }

            _onTransitionCompletedEvent.Invoke(new Transition(transition.Source, State, transition.Trigger, transition.Parameters));
        }

        private StateRepresentation EnterState(StateRepresentation representation, Transition transition, object[] args)
        {
            // Enter the new state
            representation.Enter(transition, args);

            if (FiringMode.Immediate.Equals(_firingMode) && !State.Equals(transition.Destination))
            {
                // This can happen if triggers are fired in OnEntry
                // Must update current representation with updated State
                representation = GetRepresentation(State);
                transition = new Transition(transition.Source, State, transition.Trigger, args);
            }

            // Recursively enter substates that have an initial transition
            if (representation.HasInitialTransition)
            {
                // Verify that the target state is a substate
                // Check if state has substate(s), and if an initial transition(s) has been set up.
                if (!representation.GetSubstates().Any(s => s.UnderlyingState.Equals(representation.InitialTransitionTarget)))
                {
                    throw new InvalidOperationException($"The target ({representation.InitialTransitionTarget}) for the initial transition is not a substate.");
                }

                var initialTransition = new InitialTransition(transition.Source, representation.InitialTransitionTarget, transition.Trigger, args);
                representation = GetRepresentation(representation.InitialTransitionTarget);

                // Alert all listeners of initial state transition
                _onTransitionedEvent.Invoke(new Transition(transition.Destination, initialTransition.Destination, transition.Trigger, transition.Parameters));
                representation = EnterState(representation, initialTransition, args);
            }

            return representation;
        }

        /// <summary>
        /// Override the default behaviour of throwing an exception when an unhandled trigger
        /// is fired.
        /// </summary>
        /// <param name="unhandledTriggerAction">An action to call when an unhandled trigger is fired.</param>
        public void OnUnhandledTrigger(Action<TState, TTrigger> unhandledTriggerAction)
        {
            if (unhandledTriggerAction == null) throw new ArgumentNullException(nameof(unhandledTriggerAction));
            _unhandledTriggerAction = (s, t, c) => unhandledTriggerAction(s, t);
        }

        /// <summary>
        /// Override the default behaviour of throwing an exception when an unhandled trigger
        /// is fired.
        /// </summary>
        /// <param name="unhandledTriggerAction">An action to call when an unhandled trigger is fired.</param>
        public void OnUnhandledTrigger(Action<TState, TTrigger, ICollection<string>> unhandledTriggerAction)
        {
            if (unhandledTriggerAction == null) throw new ArgumentNullException(nameof(unhandledTriggerAction));
            _unhandledTriggerAction = unhandledTriggerAction;
        }

        /// <summary>
        /// Determine if the state machine is in the supplied state.
        /// </summary>
        /// <param name="state">The state to test for.</param>
        /// <returns>True if the current state is equal to, or a substate of,
        /// the supplied state.</returns>
        public bool IsInState(TState state)
        {
            return CurrentRepresentation.IsIncludedIn(state);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state.
        /// </summary>
        /// <remarks>
        /// When the trigger is configured with parameters, the <c>default</c> value of each of the trigger parameter's types will be used 
        /// to evaluate whether it can fire, which may not be the desired behavior; to check if a trigger can be fired with specific arguments,
        /// use the overload of <c>CanFire&lt;TArg1[, TArg2[ ,TArg3]]&gt;(TriggerWithParameters&lt;TArg1[, TArg2[ ,TArg3]]&gt;, ...)</c> that
        /// matches the type arguments of your trigger.
        /// </remarks>
        /// <param name="trigger">Trigger to test.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire(TTrigger trigger)
        {
            return CurrentRepresentation.CanHandle(trigger);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state using the supplied trigger argument.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <param name="trigger">Trigger to test.</param>
        /// <param name="arg0">The first argument.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire<TArg0>(TriggerWithParameters<TArg0> trigger, TArg0 arg0)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));

            return CurrentRepresentation.CanHandle(trigger.Trigger, arg0);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state using the supplied trigger arguments.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <param name="trigger">Trigger to test.</param>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, TArg0 arg0, TArg1 arg1)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));

            return CurrentRepresentation.CanHandle(trigger.Trigger, arg0, arg1);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state using the supplied trigger arguments.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
        /// <param name="trigger">Trigger to test.</param>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="arg2">The third argument.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, TArg0 arg0, TArg1 arg1, TArg2 arg2)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));

            return CurrentRepresentation.CanHandle(trigger.Trigger, arg0, arg1, arg2);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired in the current state.
        /// </summary>
        /// <remarks>
        /// When the trigger is configured with parameters, the <c>default</c> value of each of the trigger parameter's types will be used 
        /// to evaluate whether it can fire, which may not be the desired behavior; to check if a trigger can be fired with specific arguments,
        /// use the overload of <c>CanFire&lt;TArg1[, TArg2[ ,TArg3]]&gt;(TriggerWithParameters&lt;TArg1[, TArg2[ ,TArg3]]&gt;, ...)</c> that
        /// matches the type arguments of your trigger.
        /// </remarks>
        /// <param name="trigger">Trigger to test.</param>
        /// <param name="unmetGuards">Guard descriptions of unmet guards. If given trigger is not configured for current state, this will be null.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire(TTrigger trigger, out ICollection<string> unmetGuards)
        {
            return CurrentRepresentation.CanHandle(trigger, new object[] { }, out unmetGuards);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state using the supplied trigger argument.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <param name="trigger">Trigger to test.</param>
        /// <param name="arg0">The first argument.</param>
        /// <param name="unmetGuards">Guard descriptions of unmet guards. If given trigger is not configured for current state, this will be null.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire<TArg0>(TriggerWithParameters<TArg0> trigger, TArg0 arg0, out ICollection<string> unmetGuards)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));

            return CurrentRepresentation.CanHandle(trigger.Trigger, new object[] { arg0 }, out unmetGuards);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state using the supplied trigger arguments.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <param name="trigger">Trigger to test.</param>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="unmetGuards">Guard descriptions of unmet guards. If given trigger is not configured for current state, this will be null.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, TArg0 arg0, TArg1 arg1, out ICollection<string> unmetGuards)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));

            return CurrentRepresentation.CanHandle(trigger.Trigger, new object[] { arg0, arg1 }, out unmetGuards);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state using the supplied trigger arguments.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
        /// <param name="trigger">Trigger to test.</param>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="arg2">The third argument.</param>
        /// <param name="unmetGuards">Guard descriptions of unmet guards. If given trigger is not configured for current state, this will be null.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, TArg0 arg0, TArg1 arg1, TArg2 arg2, out ICollection<string> unmetGuards)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));

            return CurrentRepresentation.CanHandle(trigger.Trigger, new object[] { arg0, arg1, arg2 }, out unmetGuards);
        }

        /// <summary>
        /// A human-readable representation of the state machine.
        /// </summary>
        /// <returns>A description of the current state and permitted triggers.</returns>
        public override string ToString()
        {
            return string.Format(
                "StateMachine {{ State = {0}, PermittedTriggers = {{ {1} }}}}",
                State,
                string.Join(", ", PermittedTriggers.Select(t => t.ToString()).ToArray()));
        }

        /// <summary>
        /// Specify the arguments that must be supplied when a specific trigger is fired.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <param name="trigger">The underlying trigger value.</param>
        /// <returns>An object that can be passed to the Fire() method in order to
        /// fire the parameterised trigger.</returns>
        public TriggerWithParameters<TArg0> SetTriggerParameters<TArg0>(TTrigger trigger)
        {
            var configuration = new TriggerWithParameters<TArg0>(trigger);
            SaveTriggerConfiguration(configuration);
            return configuration;
        }

        /// <summary>
        /// Specify the arguments that must be supplied when a specific trigger is fired.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <param name="trigger">The underlying trigger value.</param>
        /// <returns>An object that can be passed to the Fire() method in order to
        /// fire the parameterised trigger.</returns>
        public TriggerWithParameters<TArg0, TArg1> SetTriggerParameters<TArg0, TArg1>(TTrigger trigger)
        {
            var configuration = new TriggerWithParameters<TArg0, TArg1>(trigger);
            SaveTriggerConfiguration(configuration);
            return configuration;
        }

        /// <summary>
        /// Specify the arguments that must be supplied when a specific trigger is fired.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
        /// <param name="trigger">The underlying trigger value.</param>
        /// <returns>An object that can be passed to the Fire() method in order to
        /// fire the parameterised trigger.</returns>
        public TriggerWithParameters<TArg0, TArg1, TArg2> SetTriggerParameters<TArg0, TArg1, TArg2>(TTrigger trigger)
        {
            var configuration = new TriggerWithParameters<TArg0, TArg1, TArg2>(trigger);
            SaveTriggerConfiguration(configuration);
            return configuration;
        }

        void SaveTriggerConfiguration(TriggerWithParameters trigger)
        {
            if (_triggerConfiguration.ContainsKey(trigger.Trigger))
                throw new InvalidOperationException(
                    string.Format("Parameters for the trigger '{0}' have already been configured.", trigger));

            _triggerConfiguration.Add(trigger.Trigger, trigger);
        }

        void DefaultUnhandledTriggerAction(TState state, TTrigger trigger, ICollection<string> unmetGuardConditions)
        {
            if (unmetGuardConditions?.Any() ?? false)
                throw new InvalidOperationException(
                    string.Format(
                        "Trigger '{0}' is valid for transition from state '{1}' but a guard conditions are not met. Guard descriptions: '{2}'.",
                        trigger, state, string.Join(", ", unmetGuardConditions)));

            throw new InvalidOperationException(
                string.Format(
                    "No valid leaving transitions are permitted from state '{1}' for trigger '{0}'. Consider ignoring the trigger.",
                    trigger, state));
        }

        /// <summary>
        /// Registers a callback that will be invoked every time the state machine
        /// transitions from one state into another.
        /// </summary>
        /// <param name="onTransitionAction">The action to execute, accepting the details
        /// of the transition.</param>
        public void OnTransitioned(Action<Transition> onTransitionAction)
        {
            if (onTransitionAction == null) throw new ArgumentNullException(nameof(onTransitionAction));
            _onTransitionedEvent.Register(onTransitionAction);
        }

        /// <summary>
        /// Registers a callback that will be invoked every time the statemachine
        /// transitions from one state into another and all the OnEntryFrom etc methods
        /// have been invoked
        /// </summary>
        /// <param name="onTransitionAction">The action to execute, accepting the details
        /// of the transition.</param>
        public void OnTransitionCompleted(Action<Transition> onTransitionAction)
        {
            if (onTransitionAction == null) throw new ArgumentNullException(nameof(onTransitionAction));
            _onTransitionCompletedEvent.Register(onTransitionAction);
        }

        /// <summary>
        /// Unregisters a previously registered callback to prevent further events from
        /// being raised when the state machine transitions from one state into another.
        /// </summary>
        /// <param name="onTransitionAction"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public void OnTransitionedUnregister(Action<Transition> onTransitionAction)
        {
            if (onTransitionAction == null) throw new ArgumentNullException(nameof(onTransitionAction));
            _onTransitionedEvent.Unregister(onTransitionAction);
        }

        /// <summary>
        /// Unregisters a previously registered callback to prevent further events from
        /// being raised when the state machine has completed its state transition.
        /// </summary>
        /// <param name="onTransitionAction"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public void OnTransitionCompletedUnregister(Action<Transition> onTransitionAction)
        {
            if (onTransitionAction == null) throw new ArgumentNullException(nameof(onTransitionAction));
            _onTransitionCompletedEvent.Unregister(onTransitionAction);
        }

        /// <summary>
        /// Unregisters all callbacks currently registered with the state machine for
        /// both "transitioned" and "transition completed" events.
        /// </summary>
        public void UnregisterAllCallbacks()
        {
            _onTransitionedEvent.UnregisterAll();
            _onTransitionCompletedEvent.UnregisterAll();
        }

        #endregion

        #region Transitions
        /// <summary>
        /// Describes an initial state transition.
        /// </summary>
        public class InitialTransition : Transition
        {
            /// <summary>
            /// Construct a transition.
            /// </summary>
            /// <param name="source">The state transitioned from.</param>
            /// <param name="destination">The state transitioned to.</param>
            /// <param name="trigger">The trigger that caused the transition.</param>
            /// <param name="parameters">The optional trigger parameters</param>
            public InitialTransition(TState source, TState destination, TTrigger trigger, object[] parameters = null) : base(source, destination, trigger, parameters)
            {
            }
        }

        /// <summary>
        /// Describes a state transition.
        /// </summary>
        public class Transition
        {
            /// <summary>
            /// Construct a transition.
            /// </summary>
            /// <param name="source">The state transitioned from.</param>
            /// <param name="destination">The state transitioned to.</param>
            /// <param name="trigger">The trigger that caused the transition.</param>
            /// <param name="parameters">The optional trigger parameters</param>
            public Transition(TState source, TState destination, TTrigger trigger, object[] parameters = null)
            {
                Source = source;
                Destination = destination;
                Trigger = trigger;
                Parameters = parameters ?? new object[0];
            }

            /// <summary>
            /// The state transitioned from.
            /// </summary>
            public TState Source { get; }

            /// <summary>
            /// The state transitioned to.
            /// </summary>
            public TState Destination { get; }

            /// <summary>
            /// The trigger that caused the transition.
            /// </summary>
            public TTrigger Trigger { get; }

            /// <summary>
            /// True if the transition is a re-entry, i.e. the identity transition.
            /// </summary>
            public bool IsReentry => Source.Equals(Destination);

            /// <summary>
            /// The trigger parameters
            /// </summary>
            /// <remarks>
            /// Never null. For a parameterless trigger the value will be an empty array.
            /// </remarks>
            public object[] Parameters { get; }
        }

        #endregion

        #region Trigger parameters
        /// <summary>
        /// Associates configured parameters with an underlying trigger value.
        /// </summary>
        public class TriggerWithParameters
        {
            readonly TTrigger _underlyingTrigger;
            readonly Type[] _argumentTypes;

            /// <summary>
            /// Create a configured trigger.
            /// </summary>
            /// <param name="underlyingTrigger">Trigger represented by this trigger configuration.</param>
            /// <param name="argumentTypes">The argument types expected by the trigger.</param>
            public TriggerWithParameters(TTrigger underlyingTrigger, params Type[] argumentTypes)
            {
                _underlyingTrigger = underlyingTrigger;
                _argumentTypes = argumentTypes ?? throw new ArgumentNullException(nameof(argumentTypes));
            }

            /// <summary>
            /// Gets the arguments types expected by this trigger.
            /// </summary>
            public IEnumerable<Type> ArgumentTypes => _argumentTypes;

            /// <summary>
            /// Gets the underlying trigger value that has been configured.
            /// </summary>
            public TTrigger Trigger { get { return _underlyingTrigger; } }

            /// <summary>
            /// Ensure that the supplied arguments are compatible with those configured for this
            /// trigger.
            /// </summary>
            /// <param name="args"></param>
            public void ValidateParameters(object[] args)
            {
                if (args == null) throw new ArgumentNullException(nameof(args));

                ParameterConversion.Validate(args, _argumentTypes);
            }
        }

        /// <summary>
        /// A configured trigger with one required argument.
        /// </summary>
        /// <typeparam name="TArg0">The type of the first argument.</typeparam>
        public class TriggerWithParameters<TArg0> : TriggerWithParameters
        {
            /// <summary>
            /// Create a configured trigger.
            /// </summary>
            /// <param name="underlyingTrigger">Trigger represented by this trigger configuration.</param>
            public TriggerWithParameters(TTrigger underlyingTrigger)
                : base(underlyingTrigger, typeof(TArg0))
            {
            }
        }

        /// <summary>
        /// A configured trigger with two required arguments.
        /// </summary>
        /// <typeparam name="TArg0">The type of the first argument.</typeparam>
        /// <typeparam name="TArg1">The type of the second argument.</typeparam>
        public class TriggerWithParameters<TArg0, TArg1> : TriggerWithParameters
        {
            /// <summary>
            /// Create a configured trigger.
            /// </summary>
            /// <param name="underlyingTrigger">Trigger represented by this trigger configuration.</param>
            public TriggerWithParameters(TTrigger underlyingTrigger)
                : base(underlyingTrigger, typeof(TArg0), typeof(TArg1))
            {
            }
        }

        /// <summary>
        /// A configured trigger with three required arguments.
        /// </summary>
        /// <typeparam name="TArg0">The type of the first argument.</typeparam>
        /// <typeparam name="TArg1">The type of the second argument.</typeparam>
        /// <typeparam name="TArg2">The type of the third argument.</typeparam>
        public class TriggerWithParameters<TArg0, TArg1, TArg2> : TriggerWithParameters
        {
            /// <summary>
            /// Create a configured trigger.
            /// </summary>
            /// <param name="underlyingTrigger">Trigger represented by this trigger configuration.</param>
            public TriggerWithParameters(TTrigger underlyingTrigger)
                : base(underlyingTrigger, typeof(TArg0), typeof(TArg1), typeof(TArg2))
            {
            }
        }

        #endregion

        #region Configuration API
        /// <summary>
        /// The configuration for a single state value.
        /// </summary>
        public partial class StateConfiguration
        {
            private readonly StateMachine<TState, TTrigger> _machine;
            readonly StateRepresentation _representation;
            readonly Func<TState, StateRepresentation> _lookup;

            internal StateConfiguration(StateMachine<TState, TTrigger> machine, StateRepresentation representation, Func<TState, StateRepresentation> lookup)
            {
                _machine = machine;
                _representation = representation;
                _lookup = lookup;
            }

            /// <summary>
            /// The state that is configured with this configuration.
            /// </summary>
            public TState State { get { return _representation.UnderlyingState; } }

            /// <summary>
            /// The machine that is configured with this configuration.
            /// </summary>
            public StateMachine<TState, TTrigger> Machine { get { return _machine; } }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationState">The state that the trigger will cause a
            /// transition to.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration Permit(TTrigger trigger, TState destinationState)
            {
                EnforceNotIdentityTransition(destinationState);
                return InternalPermit(trigger, destinationState);
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <param name="trigger"></param>
            /// <param name="entryAction"></param>
            /// <returns></returns>
            public StateConfiguration InternalTransition(TTrigger trigger, Action<Transition> entryAction)
            {
                return InternalTransitionIf(trigger, t => true, entryAction);
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <param name="trigger"></param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="entryAction"></param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf(TTrigger trigger, Func<object[], bool> guard, Action<Transition> entryAction, string guardDescription = null)
            {
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(trigger, guard, (t, args) => entryAction(t), guardDescription));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransition(TTrigger trigger, Action internalAction)
            {
                return InternalTransitionIf(trigger, t => true, internalAction);
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf(TTrigger trigger, Func<object[], bool> guard, Action internalAction, string guardDescription = null)
            {
                if (internalAction == null) throw new ArgumentNullException(nameof(internalAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(trigger, guard, (t, args) => internalAction(), guardDescription));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf<TArg0>(TTrigger trigger, Func<object[], bool> guard, Action<Transition> internalAction, string guardDescription = null)
            {
                if (internalAction == null) throw new ArgumentNullException(nameof(internalAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(trigger, guard, (t, args) => internalAction(t), guardDescription));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransition<TArg0>(TTrigger trigger, Action<Transition> internalAction)
            {
                return InternalTransitionIf(trigger, t => true, internalAction);
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransition<TArg0>(TriggerWithParameters<TArg0> trigger, Action<TArg0, Transition> internalAction)
            {
                return InternalTransitionIf(trigger, t => true, internalAction);
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, bool> guard, Action<TArg0, Transition> internalAction, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (internalAction == null) throw new ArgumentNullException(nameof(internalAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(trigger.Trigger, TransitionGuard.ToPackedGuard(guard), (t, args) => internalAction(ParameterConversion.Unpack<TArg0>(args, 0), t), guardDescription));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransition<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger,
                Action<TArg0, TArg1, Transition> internalAction)
            {
                return InternalTransitionIf(trigger, t => true, internalAction);
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Func<object[], bool> guard, Action<TArg0, TArg1, Transition> internalAction, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (internalAction == null) throw new ArgumentNullException(nameof(internalAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(trigger.Trigger, guard, (t, args) => internalAction(
                     ParameterConversion.Unpack<TArg0>(args, 0),
                     ParameterConversion.Unpack<TArg1>(args, 1), t),
                     guardDescription));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Func<TArg0, TArg1, bool> guard, Action<TArg0, TArg1, Transition> internalAction, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (internalAction == null) throw new ArgumentNullException(nameof(internalAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(
                    trigger.Trigger,
                    TransitionGuard.ToPackedGuard(guard),
                    (t, args) => internalAction(
                    ParameterConversion.Unpack<TArg0>(args, 0),
                    ParameterConversion.Unpack<TArg1>(args, 1), t),
                    guardDescription
                    ));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <typeparam name="TArg2"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<object[], bool> guard, Action<TArg0, TArg1, TArg2, Transition> internalAction, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (internalAction == null) throw new ArgumentNullException(nameof(internalAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(trigger.Trigger, guard, (t, args) => internalAction(
                    ParameterConversion.Unpack<TArg0>(args, 0),
                    ParameterConversion.Unpack<TArg1>(args, 1),
                    ParameterConversion.Unpack<TArg2>(args, 2), t),
                    guardDescription));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <typeparam name="TArg2"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">Function that must return true in order for the trigger to be accepted.</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <param name="guardDescription">A description of the guard condition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransitionIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<TArg0, TArg1, TArg2, bool> guard, Action<TArg0, TArg1, TArg2, Transition> internalAction, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (internalAction == null) throw new ArgumentNullException(nameof(internalAction));

                _representation.AddTriggerBehaviour(new InternalTriggerBehaviour(trigger.Trigger, TransitionGuard.ToPackedGuard(guard), (t, args) => internalAction(
                    ParameterConversion.Unpack<TArg0>(args, 0),
                    ParameterConversion.Unpack<TArg1>(args, 1),
                    ParameterConversion.Unpack<TArg2>(args, 2), t),
                    guardDescription));
                return this;
            }

            /// <summary>
            /// Add an internal transition to the state machine. An internal action does not cause the Exit and Entry actions to be triggered, and does not change the state of the state machine
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <typeparam name="TArg2"></typeparam>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="internalAction">The action performed by the internal transition</param>
            /// <returns></returns>
            public StateConfiguration InternalTransition<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Action<TArg0, TArg1, TArg2, Transition> internalAction)
            {
                return InternalTransitionIf(trigger, t => true, internalAction);
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationState">The state that the trigger will cause a
            /// transition to.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitIf(TTrigger trigger, TState destinationState, Func<bool> guard, string guardDescription = null)
            {
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger,
                    destinationState,
                    new TransitionGuard(guard, guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="destinationState">State of the destination.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitIf(TTrigger trigger, TState destinationState, params Tuple<Func<bool>, string>[] guards)
            {
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger,
                    destinationState,
                    new TransitionGuard(guards));
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationState">The state that the trigger will cause a
            /// transition to.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted. Takes a single argument of type TArg0</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitIf<TArg0>(TriggerWithParameters<TArg0> trigger, TState destinationState, Func<TArg0, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger.Trigger,
                    destinationState,
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger, transition to the destination state, and guard conditions.
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted. Functions take a single argument of type TArg0.</param>
            /// <param name="destinationState">State of the destination.</param>
            /// <returns>The receiver.</returns>
            /// <returns></returns>
            public StateConfiguration PermitIf<TArg0>(TriggerWithParameters<TArg0> trigger, TState destinationState, params Tuple<Func<TArg0, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger.Trigger,
                    destinationState,
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards))
                );
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationState">The state that the trigger will cause a
            /// transition to.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted. Takes a single argument of type TArg0</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, TState destinationState, Func<TArg0, TArg1, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger.Trigger,
                    destinationState,
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription));
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted. Functions take a single argument of type TArg0.</param>
            /// <param name="destinationState">State of the destination.</param>
            /// <returns>The receiver.</returns>
            /// <returns></returns>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, TState destinationState, params Tuple<Func<TArg0, TArg1, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger.Trigger,
                    destinationState,
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards))
                );
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <typeparam name="TArg2"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationState">The state that the trigger will cause a
            /// transition to.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted. Takes a single argument of type TArg0</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, TState destinationState, Func<TArg0, TArg1, TArg2, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger.Trigger,
                    destinationState,
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription));
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <typeparam name="TArg2"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted. Functions take a single argument of type TArg0.</param>
            /// <param name="destinationState">State of the destination.</param>
            /// <returns>The receiver.</returns>
            /// <returns></returns>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, TState destinationState, params Tuple<Func<TArg0, TArg1, TArg2, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                EnforceNotIdentityTransition(destinationState);

                return InternalPermitIf(
                    trigger.Trigger,
                    destinationState,
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards))
                );
            }

            /// <summary>
            /// Accept the specified trigger, execute exit actions and re-execute entry actions.
            /// Reentry behaves as though the configured state transitions to an identical sibling state.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <returns>The receiver.</returns>
            /// <remarks>
            /// Applies to the current state only. Will not re-execute superstate actions, or
            /// cause actions to execute transitioning between super- and sub-states.
            /// </remarks>
            public StateConfiguration PermitReentry(TTrigger trigger)
            {
                return InternalPermitReentryIf(trigger, _representation.UnderlyingState, null);
            }

            /// <summary>
            /// Accept the specified trigger, execute exit actions and re-execute entry actions.
            /// Reentry behaves as though the configured state transitions to an identical sibling state.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            /// <remarks>
            /// Applies to the current state only. Will not re-execute superstate actions, or
            /// cause actions to execute transitioning between super- and sub-states.
            /// </remarks>
            public StateConfiguration PermitReentryIf(TTrigger trigger, Func<bool> guard, string guardDescription = null)
            {
                return InternalPermitReentryIf(
                    trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(guard, guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger, execute exit actions and re-execute entry actions.
            /// Reentry behaves as though the configured state transitions to an identical sibling state.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            /// <remarks>
            /// Applies to the current state only. Will not re-execute superstate actions, or
            /// cause actions to execute transitioning between super- and sub-states.
            /// </remarks>
            public StateConfiguration PermitReentryIf(TTrigger trigger, params Tuple<Func<bool>, string>[] guards)
            {
                return InternalPermitReentryIf(
                    trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(guards));
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted. Takes a single argument of type TArg0</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitReentryIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                return InternalPermitReentryIf(
                    trigger.Trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription)
                );
            }

            /// <summary>
            /// Accept the specified trigger, transition to the destination state, and guard conditions.
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted. Functions take a single argument of type TArg0.</param>
            /// <returns>The receiver.</returns>
            /// <returns></returns>
            public StateConfiguration PermitReentryIf<TArg0>(TriggerWithParameters<TArg0> trigger, params Tuple<Func<TArg0, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                return InternalPermitReentryIf(
                    trigger.Trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards))
                );
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted. Takes a single argument of type TArg0</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitReentryIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Func<TArg0, TArg1, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                return InternalPermitReentryIf(
                    trigger.Trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription)
                );
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted. Functions take a single argument of type TArg0.</param>
            /// <returns>The receiver.</returns>
            /// <returns></returns>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitReentryIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, params Tuple<Func<TArg0, TArg1, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                return InternalPermitReentryIf(
                    trigger.Trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards))
                );
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <typeparam name="TArg2"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted. Takes a single argument of type TArg0</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitReentryIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<TArg0, TArg1, TArg2, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                return InternalPermitReentryIf(
                    trigger.Trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription)
                );
            }

            /// <summary>
            ///  Accept the specified trigger, transition to the destination state, and guard condition. 
            /// </summary>
            /// <typeparam name="TArg0"></typeparam>
            /// <typeparam name="TArg1"></typeparam>
            /// <typeparam name="TArg2"></typeparam>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted. Functions take a single argument of type TArg0.</param>
            /// <returns>The receiver.</returns>
            /// <returns></returns>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitReentryIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, params Tuple<Func<TArg0, TArg1, TArg2, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                return InternalPermitReentryIf(
                    trigger.Trigger,
                    _representation.UnderlyingState,
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards))
                );
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state.
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration Ignore(TTrigger trigger)
            {
                // return IgnoreIf(trigger, NoGuard);
                // Enforce.ArgumentNotNull(guard, nameof(guard));
                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger,
                        null));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf(TTrigger trigger, Func<bool> guard, string guardDescription = null)
            {
                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger,
                        new TransitionGuard(guard, guardDescription)
                        ));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf(TTrigger trigger, params Tuple<Func<bool>, string>[] guards)
            {
                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger,
                        new TransitionGuard(guards)));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger.Trigger,
                        new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription)
                    ));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf<TArg0>(TriggerWithParameters<TArg0> trigger, params Tuple<Func<TArg0, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger.Trigger,
                        new TransitionGuard(TransitionGuard.ToPackedGuards(guards))));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf<TArg0, TArgo1>(TriggerWithParameters<TArg0, TArgo1> trigger, Func<TArg0, TArgo1, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger.Trigger,
                        new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription)
                    ));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, params Tuple<Func<TArg0, TArg1, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger.Trigger,
                        new TransitionGuard(TransitionGuard.ToPackedGuards(guards))));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<TArg0, TArg1, TArg2, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger.Trigger,
                        new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription)
                    ));
                return this;
            }

            /// <summary>
            /// Ignore the specified trigger when in the configured state, if the guard
            /// returns true..
            /// </summary>
            /// <param name="trigger">The trigger to ignore.</param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be ignored.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration IgnoreIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, params Tuple<Func<TArg0, TArg1, TArg2, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new IgnoredTriggerBehaviour(
                        trigger.Trigger,
                        new TransitionGuard(TransitionGuard.ToPackedGuards(guards))));
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when activating
            /// the configured state.
            /// </summary>
            /// <param name="activateAction">Action to execute.</param>
            /// <param name="activateActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnActivate(Action activateAction, string activateActionDescription = null)
            {
                _representation.AddActivateAction(
                    activateAction,
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when deactivating
            /// the configured state.
            /// </summary>
            /// <param name="deactivateAction">Action to execute.</param>
            /// <param name="deactivateActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnDeactivate(Action deactivateAction, string deactivateActionDescription = null)
            {
                _representation.AddDeactivateAction(
                    deactivateAction,
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <param name="entryAction">Action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntry(Action entryAction, string entryActionDescription = null)
            {
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(
                    (t, args) => entryAction(),
                    null);
                return this;

            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntry(Action<Transition> entryAction, string entryActionDescription = null)
            {
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(
                    (t, args) => entryAction(t),
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <param name="entryAction">Action to execute.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom(TTrigger trigger, Action entryAction, string entryActionDescription = null)
            {
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(
                    trigger,
                    (t, args) => entryAction(),
                    null);
                return this;

            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom(TTrigger trigger, Action<Transition> entryAction, string entryActionDescription = null)
            {
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(
                    trigger,
                    (t, args) => entryAction(t),
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom(TriggerWithParameters trigger, Action<Transition> entryAction, string entryActionDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(
                    trigger.Trigger,
                    (t, args) => entryAction(t),
                    null);
                return this;

            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom<TArg0>(TriggerWithParameters<TArg0> trigger, Action<TArg0> entryAction, string entryActionDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(
                    trigger.Trigger,
                    (t, args) => entryAction(
                        ParameterConversion.Unpack<TArg0>(args, 0)),
                    null);
                return this;

            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom<TArg0>(TriggerWithParameters<TArg0> trigger, Action<TArg0, Transition> entryAction, string entryActionDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(
                    trigger.Trigger,
                    (t, args) => entryAction(
                        ParameterConversion.Unpack<TArg0>(args, 0), t),
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Action<TArg0, TArg1> entryAction, string entryActionDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(trigger.Trigger,
                    (t, args) => entryAction(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1)),
                    null);
                return this;

            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Action<TArg0, TArg1, Transition> entryAction, string entryActionDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(trigger.Trigger, (t, args) => entryAction(
                    ParameterConversion.Unpack<TArg0>(args, 0),
                    ParameterConversion.Unpack<TArg1>(args, 1), t),
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Action<TArg0, TArg1, TArg2> entryAction, string entryActionDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(trigger.Trigger, (t, args) => entryAction(
                    ParameterConversion.Unpack<TArg0>(args, 0),
                    ParameterConversion.Unpack<TArg1>(args, 1),
                    ParameterConversion.Unpack<TArg2>(args, 2)),
                    null);
                return this;

            }

            /// <summary>
            /// Specify an action that will execute when transitioning into
            /// the configured state.
            /// </summary>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
            /// <param name="entryAction">Action to execute, providing details of the transition.</param>
            /// <param name="trigger">The trigger by which the state must be entered in order for the action to execute.</param>
            /// <param name="entryActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnEntryFrom<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Action<TArg0, TArg1, TArg2, Transition> entryAction, string entryActionDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (entryAction == null) throw new ArgumentNullException(nameof(entryAction));

                _representation.AddEntryAction(trigger.Trigger, (t, args) => entryAction(
                    ParameterConversion.Unpack<TArg0>(args, 0),
                    ParameterConversion.Unpack<TArg1>(args, 1),
                    ParameterConversion.Unpack<TArg2>(args, 2), t),
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when transitioning from
            /// the configured state.
            /// </summary>
            /// <param name="exitAction">Action to execute.</param>
            /// <param name="exitActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnExit(Action exitAction, string exitActionDescription = null)
            {
                if (exitAction == null) throw new ArgumentNullException(nameof(exitAction));

                _representation.AddExitAction(
                    t => exitAction(),
                    null);
                return this;
            }

            /// <summary>
            /// Specify an action that will execute when transitioning from
            /// the configured state.
            /// </summary>
            /// <param name="exitAction">Action to execute, providing details of the transition.</param>
            /// <param name="exitActionDescription">Action description.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration OnExit(Action<Transition> exitAction, string exitActionDescription = null)
            {
                _representation.AddExitAction(
                    exitAction,
                    null);
                return this;
            }

            /// <summary>
            /// Sets the superstate that the configured state is a substate of.
            /// </summary>
            /// <remarks>
            /// Substates inherit the allowed transitions of their superstate.
            /// When entering directly into a substate from outside of the superstate,
            /// entry actions for the superstate are executed.
            /// Likewise when leaving from the substate to outside the superstate,
            /// exit actions for the superstate will execute.
            /// </remarks>
            /// <param name="superstate">The superstate.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration SubstateOf(TState superstate)
            {
                var State = _representation.UnderlyingState;

                // Check for accidental identical cyclic configuration
                if (State.Equals(superstate))
                {
                    throw new ArgumentException($"Configuring {State} as a substate of {superstate} creates an illegal cyclic configuration.");
                }

                // Check for accidental identical nested cyclic configuration
                var superstates = new HashSet<TState> { State };

                // Build list of super states and check for
                var activeRepresentation = _lookup(superstate);
                while (activeRepresentation.Superstate != null)
                {
                    // Check if superstate is already added to hashset
                    if (superstates.Contains(activeRepresentation.Superstate.UnderlyingState))
                        throw new ArgumentException($"Configuring {State} as a substate of {superstate} creates an illegal nested cyclic configuration.");

                    superstates.Add(activeRepresentation.Superstate.UnderlyingState);
                    activeRepresentation = _lookup(activeRepresentation.Superstate.UnderlyingState);
                }

                // The check was OK, we can add this
                var superRepresentation = _lookup(superstate);
                _representation.Superstate = superRepresentation;
                superRepresentation.AddSubstate(_representation);
                return this;
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="destinationStateSelectorDescription">Optional description for the function to calculate the state </param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitDynamic(TTrigger trigger, Func<TState> destinationStateSelector,
                string destinationStateSelectorDescription = null)
            {
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                _representation.AddTriggerBehaviour(
                    new DynamicTriggerBehaviour(trigger,
                        args => destinationStateSelector(),
                        null));
                return this;
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="destinationStateSelectorDescription">Optional description of the function to calculate the state </param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            public StateConfiguration PermitDynamic<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, TState> destinationStateSelector,
                string destinationStateSelectorDescription = null)
            {
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new DynamicTriggerBehaviour(trigger.Trigger,
                        args => destinationStateSelector(
                            ParameterConversion.Unpack<TArg0>(args, 0)),
                        null));
                return this;

            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="destinationStateSelectorDescription">Optional description of the function to calculate the state </param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            public StateConfiguration PermitDynamic<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger,
                Func<TArg0, TArg1, TState> destinationStateSelector, string destinationStateSelectorDescription = null)
            {
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                if (trigger == null) throw new ArgumentNullException(nameof(trigger));

                _representation.AddTriggerBehaviour(
                    new DynamicTriggerBehaviour(trigger.Trigger, args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1)),
                    null));
                return this;
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="destinationStateSelectorDescription">Optional description of the function to calculate the state </param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
            public StateConfiguration PermitDynamic<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger,
                Func<TArg0, TArg1, TArg2, TState> destinationStateSelector, string destinationStateSelectorDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                _representation.AddTriggerBehaviour(
                    new DynamicTriggerBehaviour(trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1),
                        ParameterConversion.Unpack<TArg2>(args, 2)),
                    null));
                return this;
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitDynamicIf(TTrigger trigger, Func<TState> destinationStateSelector,
                Func<bool> guard, string guardDescription = null)
            {
                return PermitDynamicIf(trigger, destinationStateSelector, null, guard, guardDescription);
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="destinationStateSelectorDescription">Description of the function to calculate the state </param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitDynamicIf(TTrigger trigger, Func<TState> destinationStateSelector,
                string destinationStateSelectorDescription, Func<bool> guard, string guardDescription = null)
            {
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger,
                    args => destinationStateSelector(),
                    destinationStateSelectorDescription,
                    new TransitionGuard(guard, guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitDynamicIf(TTrigger trigger, Func<TState> destinationStateSelector, params Tuple<Func<bool>, string>[] guards)
            {
                return PermitDynamicIf(trigger, destinationStateSelector, null, guards);
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="destinationStateSelectorDescription">Description of the function to calculate the state </param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            public StateConfiguration PermitDynamicIf(TTrigger trigger, Func<TState> destinationStateSelector,
                string destinationStateSelectorDescription, params Tuple<Func<bool>, string>[] guards)
            {
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger,
                    args => destinationStateSelector(),
                    destinationStateSelectorDescription,
                    new TransitionGuard(guards));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, TState> destinationStateSelector, Func<bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(guard, guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, TState> destinationStateSelector)
            {
                return PermitDynamicIf<TArg0>(trigger, destinationStateSelector, new Tuple<Func<bool>, string>[0]);
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, TState> destinationStateSelector, params Tuple<Func<bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(guards));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Func<TArg0, TArg1, TState> destinationStateSelector, Func<bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(guard, guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Func<TArg0, TArg1, TState> destinationStateSelector, params Tuple<Func<bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(guards));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <returns>The receiver.</returns>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<TArg0, TArg1, TArg2, TState> destinationStateSelector, Func<bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1),
                        ParameterConversion.Unpack<TArg2>(args, 2)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(guard, guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <returns>The receiver.</returns>
            /// <param name="guards">Functions ant their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<TArg0, TArg1, TArg2, TState> destinationStateSelector, params Tuple<Func<bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1),
                        ParameterConversion.Unpack<TArg2>(args, 2)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(guards));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guard">Parameterized Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, TState> destinationStateSelector, Func<TArg0, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guards">Functions and their descriptions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0>(TriggerWithParameters<TArg0> trigger, Func<TArg0, TState> destinationStateSelector, params Tuple<Func<TArg0, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards)));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Func<TArg0, TArg1, TState> destinationStateSelector, Func<TArg0, TArg1, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guards">Functions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, Func<TArg0, TArg1, TState> destinationStateSelector, Tuple<Func<TArg0, TArg1, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards)));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guard">Function that must return true in order for the
            /// trigger to be accepted.</param>
            /// <param name="guardDescription">Guard description</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<TArg0, TArg1, TArg2, TState> destinationStateSelector, Func<TArg0, TArg1, TArg2, bool> guard, string guardDescription = null)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1),
                        ParameterConversion.Unpack<TArg2>(args, 2)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(TransitionGuard.ToPackedGuard(guard), guardDescription));
            }

            /// <summary>
            /// Accept the specified trigger and transition to the destination state, calculated
            /// dynamically by the supplied function.
            /// </summary>
            /// <param name="trigger">The accepted trigger.</param>
            /// <param name="destinationStateSelector">
            /// Function to calculate the destination state; if the source and destination states are the same, it will be reentered and 
            /// any exit or entry logic will be invoked.
            /// </param>
            /// <param name="guards">Functions that must return true in order for the
            /// trigger to be accepted.</param>
            /// <returns>The receiver.</returns>
            /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
            /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
            /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
            public StateConfiguration PermitDynamicIf<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, Func<TArg0, TArg1, TArg2, TState> destinationStateSelector, Tuple<Func<TArg0, TArg1, TArg2, bool>, string>[] guards)
            {
                if (trigger == null) throw new ArgumentNullException(nameof(trigger));
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));

                return InternalPermitDynamicIf(
                    trigger.Trigger,
                    args => destinationStateSelector(
                        ParameterConversion.Unpack<TArg0>(args, 0),
                        ParameterConversion.Unpack<TArg1>(args, 1),
                        ParameterConversion.Unpack<TArg2>(args, 2)),
                    null,    // destinationStateSelectorString
                    new TransitionGuard(TransitionGuard.ToPackedGuards(guards)));
            }

            void EnforceNotIdentityTransition(TState destination)
            {
                if (destination.Equals(_representation.UnderlyingState))
                {
                    throw new ArgumentException("Permit() (and PermitIf()) require that the destination state is not equal to the source state. To accept a trigger without changing state, use either Ignore() or PermitReentry().");
                }
            }

            StateConfiguration InternalPermit(TTrigger trigger, TState destinationState)
            {
                _representation.AddTriggerBehaviour(new TransitioningTriggerBehaviour(trigger, destinationState, null));
                return this;
            }

            StateConfiguration InternalPermitIf(TTrigger trigger, TState destinationState, TransitionGuard transitionGuard)
            {
                _representation.AddTriggerBehaviour(new TransitioningTriggerBehaviour(trigger, destinationState, transitionGuard));
                return this;
            }

            StateConfiguration InternalPermitReentryIf(TTrigger trigger, TState destinationState, TransitionGuard transitionGuard)
            {
                _representation.AddTriggerBehaviour(new ReentryTriggerBehaviour(trigger, destinationState, transitionGuard));
                return this;
            }

            StateConfiguration InternalPermitDynamicIf(TTrigger trigger, Func<object[], TState> destinationStateSelector,
                string destinationStateSelectorDescription, TransitionGuard transitionGuard)
            {
                if (destinationStateSelector == null) throw new ArgumentNullException(nameof(destinationStateSelector));
                if (transitionGuard == null) throw new ArgumentNullException(nameof(transitionGuard));

                _representation.AddTriggerBehaviour(new DynamicTriggerBehaviour(trigger,
                    destinationStateSelector,
                    transitionGuard));
                return this;
            }

            /// <summary>
            ///  Adds internal transition to this state. When entering the current state the state machine will look for an initial transition, and enter the target state.
            /// </summary>
            /// <param name="targetState">The target initial state</param>
            /// <returns>A stateConfiguration object</returns>
            public StateConfiguration InitialTransition(TState targetState)
            {
                if (_representation.HasInitialTransition) throw new InvalidOperationException($"This state has already been configured with an initial transition ({_representation.InitialTransitionTarget}).");
                if (targetState.Equals(State)) throw new ArgumentException("Setting the current state as the target destination state is not allowed.", nameof(targetState));

                _representation.SetInitialTransition(targetState);
                return this;
            }
        }

        #endregion

        #region State representation
        internal partial class StateRepresentation
        {
            readonly TState _state;

            internal IDictionary<TTrigger, ICollection<TriggerBehaviour>> TriggerBehaviours { get; } = new Dictionary<TTrigger, ICollection<TriggerBehaviour>>();
            internal ICollection<EntryActionBehavior> EntryActions { get; } = new List<EntryActionBehavior>();
            internal ICollection<ExitActionBehavior> ExitActions { get; } = new List<ExitActionBehavior>();
            internal ICollection<ActivateActionBehaviour> ActivateActions { get; } = new List<ActivateActionBehaviour>();
            internal ICollection<DeactivateActionBehaviour> DeactivateActions { get; } = new List<DeactivateActionBehaviour>();

            StateRepresentation _superstate; // null

            readonly ICollection<StateRepresentation> _substates = new List<StateRepresentation>();
            public TState InitialTransitionTarget { get; private set; } = default;

            public StateRepresentation(TState state)
            {
                _state = state;
            }

            internal ICollection<StateRepresentation> GetSubstates()
            {
                return _substates;
            }

            public bool CanHandle(TTrigger trigger, params object[] args)
            {
                return TryFindHandler(trigger, args, out TriggerBehaviourResult _);
            }

            public bool CanHandle(TTrigger trigger, object[] args, out ICollection<string> unmetGuards)
            {
                bool handlerFound = TryFindHandler(trigger, args, out TriggerBehaviourResult result);
                unmetGuards = result?.UnmetGuardConditions;
                return handlerFound;
            }

            public bool TryFindHandler(TTrigger trigger, object[] args, out TriggerBehaviourResult handler)
            {
                TriggerBehaviourResult superStateHandler = null;

                bool handlerFound = TryFindLocalHandler(trigger, args, out TriggerBehaviourResult localHandler) ||
                                    (Superstate != null && Superstate.TryFindHandler(trigger, args, out superStateHandler));

                // If no handler for super state, replace by local handler (see issue #398)
                handler = superStateHandler ?? localHandler;

                return handlerFound;
            }

            /// <summary>
            /// Gets the triggers that can be handled in this state (or one of its superstates)
            /// with the supplied trigger arguments.
            /// </summary>
            public List<TTrigger> GetPermittedTriggers(params object[] args)
            {
                var result = TriggerBehaviours
                    .Where(t => t.Value.Any(a => !a.UnmetGuardConditions(args).Any()))
                    .Select(t => t.Key)
                    .ToList();

                if (Superstate != null)
                    result = result.Union(Superstate.GetPermittedTriggers(args)).ToList();

                return result;
            }

            private bool TryFindLocalHandler(TTrigger trigger, object[] args, out TriggerBehaviourResult handlerResult)
            {
                // Get list of candidate trigger handlers
                if (!TriggerBehaviours.TryGetValue(trigger, out ICollection<TriggerBehaviour> possible))
                {
                    handlerResult = null;
                    return false;
                }
               
                // Guard functions are executed here
                var actual = possible
                    .Select(h => new TriggerBehaviourResult(h, h.UnmetGuardConditions(args)))
                    .ToArray();

                // Find a handler for the trigger
                handlerResult = TryFindLocalHandlerResult(trigger, actual)
                    ?? TryFindLocalHandlerResultWithUnmetGuardConditions(actual);

                if (handlerResult == null)
                    return false;

                return !handlerResult.UnmetGuardConditions.Any();
            }

            private TriggerBehaviourResult TryFindLocalHandlerResult(TTrigger trigger, IEnumerable<TriggerBehaviourResult> results)
            {
                var actual = results
                    .Where(r => !r.UnmetGuardConditions.Any())
                    .ToList();

                if (actual.Count <= 1)
                    return actual.FirstOrDefault();

                var message = string.Format("Multiple permitted exit transitions are configured from state '{1}' for trigger '{0}'. Guard clauses must be mutually exclusive.", trigger, _state);
                throw new InvalidOperationException(message);
            }

            private static TriggerBehaviourResult TryFindLocalHandlerResultWithUnmetGuardConditions(IEnumerable<TriggerBehaviourResult> results)
            {
                var result = results.FirstOrDefault(r => r.UnmetGuardConditions.Any());

                if (result != null)
                {
                    var unmetConditions = results.Where(r => r.UnmetGuardConditions.Any())
                                                 .SelectMany(r => r.UnmetGuardConditions);

                    // Add other unmet conditions to first result
                    foreach (var condition in unmetConditions)
                    {
                        if (!result.UnmetGuardConditions.Contains(condition))
                        {
                            result.UnmetGuardConditions.Add(condition);
                        }
                    }
                }

                return result;
            }

            public void AddActivateAction(Action action, string description = null)
            {
                ActivateActions.Add(new ActivateActionBehaviour(action));
            }

            public void AddDeactivateAction(Action action, string description = null)
            {
                DeactivateActions.Add(new DeactivateActionBehaviour(action));
            }

            public void AddEntryAction(TTrigger trigger, Action<Transition, object[]> action, string description = null)
            {
                EntryActions.Add(new EntryActionBehaviorFrom<TTrigger>(trigger, action));
            }

            public void AddEntryAction(Action<Transition, object[]> action, string description = null)
            {
                EntryActions.Add(new EntryActionBehavior(action));
            }

            public void AddExitAction(Action<Transition> action, string description = null)
            {
                ExitActions.Add(new ExitActionBehavior(action));
            }

            public void Activate()
            {
                if (_superstate != null)
                    _superstate.Activate();

                ExecuteActivationActions();
            }

            public void Deactivate()
            {
                ExecuteDeactivationActions();

                if (_superstate != null)
                    _superstate.Deactivate();
            }

            void ExecuteActivationActions()
            {
                foreach (var action in ActivateActions)
                    action.Execute();
            }

            void ExecuteDeactivationActions()
            {
                foreach (var action in DeactivateActions)
                    action.Execute();
            }

            public void Enter(Transition transition, params object[] entryArgs)
            {
                if (transition.IsReentry)
                {
                    ExecuteEntryActions(transition, entryArgs);
                }
                else if (!Includes(transition.Source))
                {
                    if (_superstate != null && !(transition is InitialTransition))
                        _superstate.Enter(transition, entryArgs);

                    ExecuteEntryActions(transition, entryArgs);
                }
            }

            public Transition Exit(Transition transition)
            {
                if (transition.IsReentry)
                {
                    ExecuteExitActions(transition);
                }
                else if (!Includes(transition.Destination))
                {
                    ExecuteExitActions(transition);

                    // Must check if there is a superstate, and if we are leaving that superstate
                    if (_superstate != null)
                    {
                        // Check if destination is within the state list
                        if (IsIncludedIn(transition.Destination))
                        {
                            // Destination state is within the list, exit first superstate only if it is NOT the the first
                            if (!_superstate.UnderlyingState.Equals(transition.Destination))
                            {
                                return _superstate.Exit(transition);
                            }
                        }
                        else
                        {
                            // Exit the superstate as well
                            return _superstate.Exit(transition);
                        }
                    }
                }
                return transition;
            }

            void ExecuteEntryActions(Transition transition, object[] entryArgs)
            {
                foreach (var action in EntryActions)
                    action.Execute(transition, entryArgs);
            }

            void ExecuteExitActions(Transition transition)
            {
                foreach (var action in ExitActions)
                    action.Execute(transition);
            }
            internal void InternalAction(Transition transition, object[] args)
            {
                InternalTriggerBehaviour internalTransition = null;

                // Look for actions in superstate(s) recursively until we hit the topmost superstate, or we actually find some trigger handlers.
                StateRepresentation aStateRep = this;
                while (aStateRep != null)
                {
                    if (aStateRep.TryFindLocalHandler(transition.Trigger, args, out TriggerBehaviourResult result))
                    {
                        // Trigger handler found in this state
                        internalTransition = result.Handler as InternalTriggerBehaviour;
                        break;
                    }
                    // Try to look for trigger handlers in superstate (if it exists)
                    aStateRep = aStateRep._superstate;
                }

                // Execute internal transition event handler
                if (internalTransition == null) throw new ArgumentNullException("The configuration is incorrect, no action assigned to this internal transition.");
                internalTransition.InternalAction(transition, args);
            }
            public void AddTriggerBehaviour(TriggerBehaviour triggerBehaviour)
            {
                if (!TriggerBehaviours.TryGetValue(triggerBehaviour.Trigger, out ICollection<TriggerBehaviour> allowed))
                {
                    allowed = new List<TriggerBehaviour>();
                    TriggerBehaviours.Add(triggerBehaviour.Trigger, allowed);
                }
                allowed.Add(triggerBehaviour);
            }

            public StateRepresentation Superstate
            {
                get
                {
                    return _superstate;
                }
                set
                {
                    _superstate = value;
                }
            }

            public TState UnderlyingState
            {
                get
                {
                    return _state;
                }
            }

            public void AddSubstate(StateRepresentation substate)
            {
                _substates.Add(substate);
            }

            /// <summary>
            /// Checks if the state is in the set of this state or its sub-states
            /// </summary>
            /// <param name="state">The state to check</param>
            /// <returns>True if included</returns>
            public bool Includes(TState state)
            {
                return _state.Equals(state) || _substates.Any(s => s.Includes(state));
            }

            /// <summary>
            /// Checks if the state is in the set of this state or a super-state
            /// </summary>
            /// <param name="state">The state to check</param>
            /// <returns>True if included</returns>
            public bool IsIncludedIn(TState state)
            {
                return
                    _state.Equals(state) ||
                    (_superstate != null && _superstate.IsIncludedIn(state));
            }
            
            internal void SetInitialTransition(TState state)
            {
                InitialTransitionTarget = state;
                HasInitialTransition = true;
            }
            public bool HasInitialTransition { get; private set; }
        }

        #endregion

        #region Trigger behaviours and guards
        internal abstract class TriggerBehaviour
        {
            /// <summary>
            /// If there is no guard function, _guard is set to TransitionGuard.Empty
            /// </summary>
            readonly TransitionGuard _guard;

            /// <summary>
            /// TriggerBehaviour constructor
            /// </summary>
            /// <param name="trigger">The accepted trigger</param>
            /// <param name="guard">TransitionGuard (null if no guard function)</param>
            protected TriggerBehaviour(TTrigger trigger, TransitionGuard guard)
            {
                _guard = guard ?? TransitionGuard.Empty;
                Trigger = trigger;
            }

            public TTrigger Trigger { get; }

            /// <summary>
            /// Guard is the transition guard for this trigger. Equal to
            /// TransitionGuard.Empty if there is no transition guard.
            /// </summary>
            internal TransitionGuard Guard => _guard;

            /// <summary>
            /// Guards is the list of guard functions for the transition guard for this trigger
            /// </summary>
            internal ICollection<Func<object[], bool>> Guards => _guard.Guards;

            /// <summary>
            /// GuardConditionsMet is true if all the guard functions return true
            /// or if there are no guard functions
            /// </summary>
            public bool GuardConditionsMet(params object[] args) => _guard.GuardConditionsMet(args);

            /// <summary>
            /// UnmetGuardConditions is a list of the descriptions of all guard conditions
            /// whose guard function returns false
            /// </summary>
            public ICollection<string> UnmetGuardConditions(object[] args) => _guard.UnmetGuardConditions(args);
        }

        internal class TransitioningTriggerBehaviour : TriggerBehaviour
        {
            internal TState Destination { get; }

            // transitionGuard can be null if there is no guard function on the transition
            public TransitioningTriggerBehaviour(TTrigger trigger, TState destination, TransitionGuard transitionGuard)
                : base(trigger, transitionGuard)
            {
                Destination = destination;
            }
        }

        internal class ReentryTriggerBehaviour : TriggerBehaviour
        {
            readonly TState _destination;

            internal TState Destination { get { return _destination; } }

            // transitionGuard can be null if there is no guard function on the transition
            public ReentryTriggerBehaviour(TTrigger trigger, TState destination, TransitionGuard transitionGuard)
                : base(trigger, transitionGuard)
            {
                _destination = destination;
            }
        }

        internal class IgnoredTriggerBehaviour : TriggerBehaviour
        {
            public IgnoredTriggerBehaviour(TTrigger trigger, TransitionGuard transitionGuard)
                : base(trigger, transitionGuard)
            {
            }
        }

        internal class DynamicTriggerBehaviour : TriggerBehaviour
        {
            readonly Func<object[], TState> _destination;

            public DynamicTriggerBehaviour(TTrigger trigger, Func<object[], TState> destination,
                TransitionGuard transitionGuard)
                : base(trigger, transitionGuard)
            {
                _destination = destination ?? throw new ArgumentNullException(nameof(destination));
            }

            public void GetDestinationState(TState source, object[] args, out TState destination)
            {
                destination = _destination(args);
            }
        }

        internal class InternalTriggerBehaviour : TriggerBehaviour
        {
            public Action<Transition, object[]> InternalAction { get; }

            public InternalTriggerBehaviour(TTrigger trigger, Func<object[], bool> guard, Action<Transition, object[]> internalAction, string guardDescription = null)
                : base(trigger, new TransitionGuard(guard, guardDescription))
            {
                InternalAction = internalAction;
            }
        }

        internal class GuardCondition
        {
            /// <summary>
            /// Description used for compiler generated (lambda) guards when the caller did not
            /// supply one. Kept identical to the value the standard library used.
            /// </summary>
            internal const string DefaultFunctionDescription = "Function";

            static readonly char[] MethodNameChars = { '<', '>', '`' };

            /// <summary>
            /// Constructor that takes in a guard with no argument.
            /// The guard is wrapped in a lambda, so the original method description is captured
            /// before wrapping to keep guard descriptions meaningful.
            /// </summary>
            /// <param name="guard">No argument guard condition</param>
            /// <param name="description">Caller supplied description; may be null</param>
            internal GuardCondition(Func<bool> guard, string description)
                : this(args => guard(), description ?? Describe(guard))
            {
            }

            internal GuardCondition(Func<object[], bool> guard, string description)
            {
                Guard = guard ?? throw new ArgumentNullException(nameof(guard));
                Description = description ?? Describe(guard);
            }

            internal Func<object[], bool> Guard { get; }

            /// <summary>
            /// Description of the guard condition: the caller-defined description if one was
            /// provided, otherwise a human readable form of the guard method name.
            /// </summary>
            internal string Description { get; }

            static string Describe(Delegate guard)
            {
                var name = guard?.Method?.Name;
                if (name == null) return DefaultFunctionDescription;
                return name.IndexOfAny(MethodNameChars) >= 0 ? DefaultFunctionDescription : name;
            }
        }

        internal class TransitionGuard
        {
            internal IList<GuardCondition> Conditions { get; }

            public static readonly TransitionGuard Empty = new TransitionGuard(new Tuple<Func<object[],bool>, string>[0]);

            #region Generic TArg0, ... to object[] converters

            public static Func<object[], bool> ToPackedGuard<TArg0>(Func<TArg0, bool> guard)
            {
                return args => guard(ParameterConversion.Unpack<TArg0>(args, 0));
            }

            public static Func<object[], bool> ToPackedGuard<TArg0, TArg1>(Func<TArg0, TArg1, bool> guard)
            {
                return args => guard(
                    ParameterConversion.Unpack<TArg0>(args, 0), 
                    ParameterConversion.Unpack<TArg1>(args, 1));
            }

            public static Func<object[], bool> ToPackedGuard<TArg0, TArg1, TArg2>(Func<TArg0, TArg1, TArg2, bool> guard)
            {
                return args => guard(
                    ParameterConversion.Unpack<TArg0>(args, 0),
                    ParameterConversion.Unpack<TArg1>(args, 1),
                    ParameterConversion.Unpack<TArg2>(args, 2));
            }

            public static Tuple<Func<object[], bool>, string>[] ToPackedGuards<TArg0>(Tuple<Func<TArg0, bool>, string>[] guards)
            {
                return guards.Select(guard => new Tuple<Func<object[], bool>, string>(
                        ToPackedGuard(guard.Item1), guard.Item2))
                    .ToArray();
            }

            public static Tuple<Func<object[], bool>, string>[] ToPackedGuards<TArg0, TArg1>(Tuple<Func<TArg0, TArg1, bool>, string>[] guards)
            {
                return guards.Select(guard => new Tuple<Func<object[], bool>, string>(
                        ToPackedGuard(guard.Item1), guard.Item2))
                    .ToArray();
            }

            public static Tuple<Func<object[], bool>, string>[] ToPackedGuards<TArg0, TArg1, TArg2>(Tuple<Func<TArg0, TArg1, TArg2, bool>, string>[] guards)
            {
                return guards.Select(guard => new Tuple<Func<object[], bool>, string>(
                        ToPackedGuard(guard.Item1), guard.Item2))
                    .ToArray();
            }

            #endregion

            internal TransitionGuard(Tuple<Func<bool>, string>[] guards)
            {
                Conditions = guards
                    .Select(g => new GuardCondition(g.Item1, g.Item2))
                    .ToList();
            }

            internal TransitionGuard(Func<bool> guard, string description = null)
            {
                Conditions = new List<GuardCondition>
                {
                    new GuardCondition(guard, description)
                };
            }

            internal TransitionGuard(Tuple<Func<object[], bool>, string>[] guards)
            {
                Conditions = guards
                    .Select(g => new GuardCondition(g.Item1, g.Item2))
                    .ToList();
            }

            internal TransitionGuard(Func<object[], bool> guard, string description = null)
            {
                Conditions = new List<GuardCondition>
                {
                    new GuardCondition(guard, description)
                };
            }
            
            /// <summary>
            /// Guards is the list of the guard functions for all guard conditions for this transition
            /// </summary>
            internal ICollection<Func<object[], bool>> Guards => Conditions.Select(g => g.Guard).ToList();

            /// <summary>
            /// GuardConditionsMet is true if all of the guard functions return true
            /// or if there are no guard functions
            /// </summary>
            public bool GuardConditionsMet(object[] args)
            {
                return Conditions.All(c => c.Guard == null || c.Guard(args));
            }

            /// <summary>
            /// UnmetGuardConditions is a list of the descriptions of all guard conditions
            /// whose guard function returns false
            /// </summary>
            public ICollection<string> UnmetGuardConditions(object[] args)
            {
                return Conditions
                    .Where(c => !c.Guard(args))
                    .Select(c => c.Description)
                    .ToList();
            }
        }

        #endregion

        #region Entry, exit and activation behaviours
        internal class EntryActionBehavior
        {
            readonly Action<Transition, object[]> _action;

            public EntryActionBehavior(Action<Transition, object[]> action)
            {
                _action = action;
            }

            public virtual void Execute(Transition transition, object[] args)
            {
                _action(transition, args);
            }
        }

        internal class EntryActionBehaviorFrom<TTriggerType> : EntryActionBehavior
        {
            internal TTriggerType Trigger { get; }

            public EntryActionBehaviorFrom(TTriggerType trigger, Action<Transition, object[]> action)
                : base(action)
            {
                Trigger = trigger;
            }

            public override void Execute(Transition transition, object[] args)
            {
                if (transition.Trigger.Equals(Trigger))
                    base.Execute(transition, args);
            }
        }

        internal class ExitActionBehavior
        {
            readonly Action<Transition> _action;

            public ExitActionBehavior(Action<Transition> action)
            {
                _action = action;
            }

            public void Execute(Transition transition)
            {
                _action(transition);
            }
        }

        internal class ActivateActionBehaviour
        {
            readonly Action _action;

            public ActivateActionBehaviour(Action action)
            {
                _action = action;
            }

            public void Execute()
            {
                _action();
            }
        }

        internal class DeactivateActionBehaviour
        {
            readonly Action _action;

            public DeactivateActionBehaviour(Action action)
            {
                _action = action;
            }

            public void Execute()
            {
                _action();
            }
        }

        #endregion

        #region Infrastructure
        internal class StateReference
        {
            public TState State { get; set; }
        }

        internal class TriggerBehaviourResult
        {
            public TriggerBehaviourResult(TriggerBehaviour handler, ICollection<string> unmetGuardConditions)
            {
                Handler = handler;
                UnmetGuardConditions = unmetGuardConditions;
            }

            public TriggerBehaviour Handler { get; }
            public ICollection<string> UnmetGuardConditions { get; }
        }

        internal class OnTransitionedEvent
        {
            event Action<Transition> _onTransitioned;

            public void Invoke(Transition transition)
            {
                _onTransitioned?.Invoke(transition);
            }

            public void Register(Action<Transition> action)
            {
                _onTransitioned -= action;
                _onTransitioned += action;
            }

            public void Unregister(Action<Transition> action)
            {
                if (_onTransitioned != null)
                {
                    _onTransitioned -= action;
                }
            }

            public void UnregisterAll()
            {
                if (_onTransitioned != null)
                {
                    foreach (Delegate eventHandler in _onTransitioned.GetInvocationList())
                    {
                        _onTransitioned -= (Action<Transition>)eventHandler;
                    }
                }
            }
        }

        #endregion
    }

    #region Parameter conversion
    static class ParameterConversion
    {
        public static object Unpack(object[] args, Type argType, int index)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            
            if (args.Length == 0)
                return null;
            
            if (args.Length <= index || index < 0)
                throw new ArgumentException(
                    string.Format("An argument of type {0} is required in position {1}.", argType, index));

            var arg = args[index];

            if (arg != null && !argType.IsAssignableFrom(arg.GetType()))
                throw new ArgumentException(
                    string.Format("The argument in position {0} is of type {1} but must be of type {2}.", index, arg.GetType(), argType));

            return arg;
        }

        public static TArg Unpack<TArg>(object[] args, int index)
        {
            if (args.Length == 0) return default;

            return (TArg)Unpack(args, typeof(TArg), index);
        }

        public static void Validate(object[] args, Type[] expected)
        {
            if (args.Length > expected.Length)
                throw new ArgumentException(
                    string.Format("Too many parameters have been supplied. Expecting {0} but got {1}.", expected.Length, args.Length));

            for (int i = 0; i < expected.Length; ++i)
                Unpack(args, expected[i], i);
        }
    }
    #endregion
}
