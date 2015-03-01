﻿using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.Actor.Internals;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Util;
using Akka.Util.Internal;

namespace Akka.Routing
{
    /// <summary>
    /// Configuration for router actors
    /// </summary>
    public abstract class RouterConfig : IEquatable<RouterConfig> , ISurrogated
    {
        //  public abstract RoutingLogic GetLogic();

        public static readonly RouterConfig NoRouter = new NoRouter();
        private readonly string _routerDispatcher;

        public virtual string RouterDispatcher
        {
            get { return _routerDispatcher; }
        }

        public virtual RouterConfig WithFallback(RouterConfig routerConfig)
        {
            return this;
        }

        public abstract Router CreateRouter(ActorSystem system);
        internal abstract RouterActor CreateRouterActor();

        public abstract IEnumerable<Routee> GetRoutees(RoutedActorCell routedActorCell);

        public virtual bool IsManagementMessage(object message)
        {
            return
                message is AutoReceivedMessage ||
                // in akka.net this message is a subclass of AutoReceivedMessage - so removed condition that "message is Terminated ||"
                message is RouterManagementMesssage;
        }

        public virtual bool Equals(RouterConfig other)
        {
            if (other == null) return false;
            return GetType() == other.GetType() && String.Equals(RouterDispatcher, other.RouterDispatcher);
        }

        public abstract ISurrogate ToSurrogate(ActorSystem system);

        protected RouterConfig()
        {            
        }
        protected RouterConfig(string routerDispatcher)
        {
            _routerDispatcher = routerDispatcher;
        }
    }

    public static class RouterConfigExtensions
    {
        public static bool NoRouter(this RouterConfig config)
        {
            return config == null || config is NoRouter;
        }
    }

    /// <summary>
    /// Signals that no Router is to be used with a given <see cref="Props"/>
    /// </summary>
    public class NoRouter : RouterConfig
    {
        internal override RouterActor CreateRouterActor()
        {
            throw new NotImplementedException();
        }

        public override Router CreateRouter(ActorSystem system)
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<Routee> GetRoutees(RoutedActorCell routedActorCell)
        {
            throw new NotImplementedException();
        }

        public class NoRouterSurrogate : ISurrogate
        {

            public ISurrogated FromSurrogate(ActorSystem system)
            {
                return new NoRouter();
            }
        }

        public override ISurrogate ToSurrogate(ActorSystem system)
        {
            return new NoRouterSurrogate();
        }
    }

    /// <summary>
    /// Base class for defining Group routers.
    /// </summary>
    public abstract class Group : RouterConfig
    {
        private readonly string[] _paths;

        public string[] Paths
        {
            get { return _paths; }            
        }

        protected Group(IEnumerable<string> paths)
        {
            _paths = paths.ToArray();
        }

        protected Group(IEnumerable<string> paths,string routerDispatcher) : base(routerDispatcher)
        {
            _paths = paths.ToArray();
        }

        protected Group(IEnumerable<ActorRef> routees)
        {
            _paths = routees.Select(x => x.Path.ToStringWithAddress()).ToArray();
        }

        /// <summary>
        /// INTERNAL API
        /// </summary>
        internal Routee RouteeFor(string path, IActorContext context)
        {
            return new ActorSelectionRoutee(context.ActorSelection(path));
        }

        public Props Props()
        {
            return Actor.Props.Empty.WithRouter(this);
        }

        internal override RouterActor CreateRouterActor()
        {
            return new RouterActor();
        }

        public override Router CreateRouter(ActorSystem system)
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<Routee> GetRoutees(RoutedActorCell routedActorCell)
        {
            if (_paths == null) return new Routee[0];
            return _paths.Select(((ActorSystemImpl)routedActorCell.System).ActorSelection).Select(actor => new ActorSelectionRoutee(actor));
        }

        public override bool Equals(RouterConfig other)
        {
            if (!base.Equals(other)) return false;
            var otherGroup = other as Group;
            if (otherGroup == null) return false; //should never be true due to the previous check
            return Paths.Intersect(otherGroup.Paths).Count() == Paths.Length;
        }
    }


    /// <summary>
    /// Base class for defining Pool routers
    /// </summary>
    public abstract class Pool : RouterConfig, IEquatable<Pool>
    {
        private readonly int _nrOfInstances;
        private readonly bool _usePoolDispatcher;
        private readonly Resizer _resizer;
        private readonly SupervisorStrategy _supervisorStrategy;
        //TODO: add supervisor strategy to the equality compare
        public bool Equals(Pool other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Resizer, other.Resizer) && UsePoolDispatcher.Equals(other.UsePoolDispatcher) && NrOfInstances == other.NrOfInstances;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Pool) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = (Resizer != null ? Resizer.GetHashCode() : 0);
                hashCode = (hashCode*397) ^ UsePoolDispatcher.GetHashCode();
                hashCode = (hashCode*397) ^ NrOfInstances;
                return hashCode;
            }
        }

        protected Pool(int nrOfInstances, Resizer resizer, SupervisorStrategy supervisorStrategy, string routerDispatcher,
            bool usePoolDispatcher = false) :base(routerDispatcher)
        {
            // OMG, if every member in Java is virtual - you must never call any members in a constructor!!1!
            // In all seriousness, without making these members virtual RemoteRouterConfig won't work
            // ReSharper disable DoNotCallOverridableMethodsInConstructor
            _nrOfInstances = nrOfInstances;

            _resizer = resizer;
            _supervisorStrategy = supervisorStrategy ?? DefaultStrategy;
            _usePoolDispatcher = usePoolDispatcher;
        }

        protected Pool(Config config)
        {
            _nrOfInstances = config.GetInt("nr-of-instances");
            _resizer = DefaultResizer.FromConfig(config);
            _usePoolDispatcher = config.HasPath("pool-dispatcher");
            // ReSharper restore DoNotCallOverridableMethodsInConstructor
        }

        /// <summary>
        /// The number of instances in the pool.
        /// </summary>
        public virtual int NrOfInstances
        {
            get { return _nrOfInstances; }
        }

        /// <summary>
        /// Whether or not to use the pool dispatcher.
        /// </summary>
        public virtual bool UsePoolDispatcher
        {
            get { return _usePoolDispatcher; }
        }

        /// <summary>
        /// An instance of the resizer for this pool.
        /// </summary>
        public virtual Resizer Resizer
        {
            get { return _resizer; }
        }

        /// <summary>
        /// An instance of the supervisor strategy for this pool.
        /// </summary>
        public virtual SupervisorStrategy SupervisorStrategy
        {
            get { return _supervisorStrategy; }
        }

        public virtual Routee NewRoutee(Props routeeProps, IActorContext context)
        {
            var routee = new ActorRefRoutee(context.ActorOf(EnrichWithPoolDispatcher(routeeProps, context)));
            return routee;
        }

        internal Props EnrichWithPoolDispatcher(Props routeeProps, IActorContext context)
        {
            //        if (usePoolDispatcher && routeeProps.dispatcher == Dispatchers.DefaultDispatcherId)
            //  routeeProps.withDispatcher("akka.actor.deployment." + context.self.path.elements.drop(1).mkString("/", "/", "")
            //    + ".pool-dispatcher")
            //else
            //  routeeProps
            if (UsePoolDispatcher && routeeProps.Dispatcher == Dispatchers.DefaultDispatcherId)
            {
                return
                    routeeProps.WithDispatcher("akka.actor.deployment." + context.Self.Path.Elements.Drop(1).Join("/") +
                                               ".pool-dispatcher");
            }
            return routeeProps;
        }

        public Props Props(Props routeeProps)
        {
            return routeeProps.WithRouter(this);
        }

        internal override RouterActor CreateRouterActor()
        {
            if (Resizer == null)
                return new RouterPoolActor(SupervisorStrategy);
            return new ResizablePoolActor(SupervisorStrategy);
        }

        public override IEnumerable<Routee> GetRoutees(RoutedActorCell routedActorCell)
        {
            for (int i = 0; i < NrOfInstances; i++)
            {
                //TODO: where do we get props?
                yield return NewRoutee(Actor.Props.Empty, routedActorCell);
            }
        }

        #region Static methods

        /// <summary>
        ///     When supervisorStrategy is not specified for an actor this
        ///     is used by default. OneForOneStrategy with a decider which escalates by default.
        /// </summary>
        public static SupervisorStrategy DefaultStrategy
        {
            get { return new OneForOneStrategy(10, TimeSpan.FromSeconds(10), ex => Directive.Escalate); }
        }

        #endregion

        #region Overrides

        //public override bool Equals(RouterConfig other)
        //{
        //    if (!base.Equals(other)) return false;
        //    var otherPool = other as Pool;
        //    if (otherPool == null) return false; //should never be true due to the previous check
        //    return NrOfInstances == otherPool.NrOfInstances &&
        //           UsePoolDispatcher == otherPool.UsePoolDispatcher &&
        //           (Resizer == null && otherPool.Resizer == null || Resizer != null && otherPool.Resizer != null) &&
        //           SupervisorStrategy.GetType() == otherPool.SupervisorStrategy.GetType();
        //}

        #endregion
    }

    public class FromConfig : RouterConfig
    {
        private static readonly FromConfig _instance = new FromConfig();

        public static FromConfig Instance
        {
            get { return _instance; }
        }

        public override Router CreateRouter(ActorSystem system)
        {
            throw new NotSupportedException();
        }

        internal override RouterActor CreateRouterActor()
        {
            throw new NotSupportedException();
        }

        public override IEnumerable<Routee> GetRoutees(RoutedActorCell routedActorCell)
        {
            throw new NotSupportedException();
        }

        public class FromConfigSurrogate : ISurrogate
        {

            public ISurrogated FromSurrogate(ActorSystem system)
            {
                return Instance;
            }
        }

        public override ISurrogate ToSurrogate(ActorSystem system)
        {
            return new FromConfigSurrogate();
        }
    }
}