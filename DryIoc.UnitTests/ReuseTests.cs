﻿using System;
using System.Threading;
using DryIoc.UnitTests.CUT;
using NUnit.Framework;

namespace DryIoc.UnitTests
{
    [TestFixture]
    public class ReuseTests
    {
        [Test]
        public void When_container_disposed_Then_factory_call_should_throw()
        {
            var container = new Container();
            container.Register(typeof(IService), typeof(DisposableService), Reuse.Singleton);

            var factory = container.Resolve<Func<IService>>();
            container.Dispose();

            var ex = Assert.Throws<ContainerException>(() => factory());
            Assert.That(ex.Message, Is.StringContaining("is disposed and its operations are no longer available"));
        }

        [Test]
        public void When_container_disposed_Then_factory_call_should_throw2()
        {
            var container = new Container();
            container.Register(typeof(IService), typeof(DisposableService), Reuse.Singleton);

            var service = (DisposableService)container.Resolve<IService>();
            container.Dispose();

            Assert.That(service.IsDisposed, Is.True);
        }

        [Test]
        public void When_registring_external_instance_and_disposing_container_Then_instance_should_not_be_disposed()
        {
            var container = new Container();
            var service = new DisposableService();
            container.RegisterInstance<IService>(service);

            container.Dispose();

            Assert.That(service.IsDisposed, Is.False);
        }

        [Test]
        public void Scopes_parameter_with_the_same_name_as_Container_scope_wont_collide()
        {
            var container = new Container();
            container.Register<Soose>(Reuse.Singleton);

            var factory = container.Resolve<Func<int, Soose>>();

            Assert.That(factory(1).Scopes, Is.EqualTo(1));
        }

        [Test]
        public void In_same_resolution_scope_Log_instances_should_be_same()
        {
            var container = new Container();
            container.Register<Consumer>();
            container.Register<Account>();
            container.Register<Log>(Reuse.InResolutionScope);

            var consumer = container.Resolve<Consumer>();
            //var consumerExpr = container.Resolve<Container.DebugExpression<Consumer>>();

            Assert.That(consumer.Log, Is.Not.Null.And.SameAs(consumer.Account.Log));
        }

        [Test]
        public void In_different_resolution_scopes_Log_instances_should_be_different()
        {
            var container = new Container();
            container.Register<Consumer>();
            container.Register<Account>();
            container.Register<Log>(Reuse.InResolutionScope);

            var consumer = container.Resolve<Consumer>();
            var account = container.Resolve<Account>();

            Assert.That(consumer.Log, Is.Not.Null.And.Not.SameAs(account.Log));
        }

        [Test, Explicit]
        public void For_signleton_injected_as_Func_and_as_instance_only_one_instance_should_be_created()
        {
            ServiceWithInstanceCountWithStringParam.InstanceCount = 0;
            try
            {
                var container = new Container();
                container.Register<ClientWithFuncAndInstanceDependency>();
                container.Register<IService, ServiceWithInstanceCountWithStringParam>(Reuse.Singleton);
                container.RegisterInstance("I am a string");

                container.Resolve<ClientWithFuncAndInstanceDependency>();

                Assert.That(ServiceWithInstanceCountWithStringParam.InstanceCount, Is.EqualTo(1));
            }
            finally
            {
                ServiceWithInstanceCountWithStringParam.InstanceCount = 0;
            }
        }

        [Test]
        public void Given_Thread_reuse_and_no_open_scope_Exception_should_be_thrown()
        {
            var container = new Container();
            container.Register<Service>(Reuse.InThreadScope);

            var ex = Assert.Throws<ContainerException>(() => container.Resolve<Service>());

            Assert.That(ex.Message, Is.StringContaining("No current scope available"));
        }

        [Test]
        public void Given_Thread_reuse_Services_resolved_in_same_thread_should_be_the_same()
        {
            using (var container = new Container().OpenScope())
            {
                container.Register<Service>(Reuse.InThreadScope);

                var one = container.Resolve<Service>();
                var another = container.Resolve<Service>();

                Assert.That(one, Is.SameAs(another));
            }
        }

        [Test]
        public void Given_Thread_reuse_Services_resolved_in_same_thread_should_be_the_same_In_nested_scope_too()
        {
            using (var container = new Container().OpenScope())
            {
                container.Register<Service>(Reuse.InThreadScope);

                var one = container.Resolve<Service>();

                using (var nested = container.OpenScope())
                {
                    var two = nested.Resolve<Service>();
                    Assert.That(one, Is.SameAs(two));
                }

                var another = container.Resolve<Service>();
                Assert.That(one, Is.SameAs(another));
            }
        }

        [Test]
        public void Given_Thread_reuse_Dependencies_injected_in_same_thread_should_be_the_same()
        {
            var container = new Container().OpenScope();
            container.Register<ServiceWithDependency>();
            container.Register<IDependency, Dependency>(Reuse.InThreadScope);

            var one = container.Resolve<ServiceWithDependency>();
            var another = container.Resolve<ServiceWithDependency>();
            Assert.That(one.Dependency, Is.SameAs(another.Dependency));
        }

        [Test]
        public void Given_Thread_reuse_Services_resolved_in_different_thread_should_be_the_different()
        {
            var container = new Container();
            var mainThread = container.OpenScope();
            mainThread.Register<Service>(Reuse.InThreadScope);

            Service one = null;
            var threadOne = new Thread(() =>
            {
                using (var threadLocal = container.OpenScope())
                    one = threadLocal.Resolve<Service>();
            }) { IsBackground = true };

            threadOne.Start();

            var another = mainThread.Resolve<Service>();

            threadOne.Join();
            Assert.That(one, Is.Not.SameAs(another));
        }

        [Test]
        public void Given_Thread_reuse_Dependencies_injected_in_different_thread_should_be_different()
        {
            var parent = new Container();
            var container = parent.OpenScope();
            container.Register<ServiceWithDependency>();
            container.Register<IDependency, Dependency>(Reuse.InThreadScope);

            ServiceWithDependency one = null;
            var threadOne = new Thread(() =>
            {
                using (var otherThread = parent.OpenScope())
                    one = otherThread.Resolve<ServiceWithDependency>();
            });
            threadOne.Start();

            var another = container.Resolve<ServiceWithDependency>();

            threadOne.Join();
            Assert.That(one.Dependency, Is.Not.SameAs(another.Dependency));
        }

        [Test]
        public void Can_use_both_resolution_scope_and_singleton_reuse_in_same_resolution_root()
        {
            var container = new Container();
            container.Register<ServiceWithResolutionAndSingletonDependencies>();
            container.Register<SingletonDep>(Reuse.InResolutionScope);
            container.Register<ResolutionScopeDep>(Reuse.Singleton);

            var service = container.Resolve<ServiceWithResolutionAndSingletonDependencies>();

            Assert.That(service.ResolutionScopeDep, Is.SameAs(service.SingletonDep.ResolutionScopeDep));
        }

        [Test]
        public void Should_throw_if_rule_specified_and_dependency_lifespan_is_less_than_parents()
        {
            var container = new Container(rules => rules.EnableThrowIfDepenedencyHasShorterReuseLifespan(true));
            container.Register<Client>(Reuse.Singleton);
            container.Register<ILogger, FastLogger>(Reuse.InResolutionScope);

            var ex = Assert.Throws<ContainerException>(() =>
                container.Resolve<Client>());

            Assert.That(ex.Error, Is.EqualTo(Error.DEPENDENCY_HAS_SHORTER_REUSE_LIFESPAN));
            Assert.That(ex.Message, Is.StringContaining(
                "Dependency DryIoc.UnitTests.FastLogger: DryIoc.UnitTests.ILogger as parameter \"logger\" has shorter Reuse lifespan than its parent: DryIoc.UnitTests.Client." + Environment.NewLine +
                "ResolutionScopeReuse:10 lifetime is shorter than SingletonReuse:1000." + Environment.NewLine +
                "You may turn Off this error with new Container(rules=>rules.EnableThrowIfDepenedencyHasShorterReuseLifespan(false))."));
        }

        [Test]
        public void Should_Not_throw_if_rule_is_off_and_dependency_lifespan_is_less_than_parents()
        {
            var container = new Container(rules => rules.EnableThrowIfDepenedencyHasShorterReuseLifespan(false));
            container.Register<Client>(Reuse.Singleton);
            container.Register<ILogger, FastLogger>(Reuse.InResolutionScope);

            var client = container.Resolve<Client>();

            Assert.That(client.Logger, Is.InstanceOf<FastLogger>());
        }

        [Test]
        public void Can_replace_singleton_reuse_with_transient_in_container()
        {
            var container = new Container(rules => rules
                .WithReuseMapping((reuse, _) => reuse is SingletonReuse ? Reuse.Transient : reuse));

            container.Register<Service>(Reuse.Singleton);

            var one = container.Resolve<Service>();
            var two = container.Resolve<Service>();

            Assert.That(one, Is.Not.SameAs(two));
        }

        [Test]
        public void Can_disposed_resolution_reused_services()
        {
            var container = new Container();
            container.Register<SomeDep>(Reuse.InResolutionScope);
            container.Register<SomeRoot>();

            var service = container.Resolve<ResolutionScoped<SomeRoot>>();
            using (service)
                Assert.That(service.Scope, Is.Not.Null);

            Assert.That(service.Value.Dep.IsDisposed, Is.True);
        }


        internal class SomeDep : IDisposable
        {
            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }

        internal class SomeRoot
        {
            public SomeDep Dep { get; private set; }
            public SomeRoot(SomeDep dep)
            {
                Dep = dep;
            }
        }

        // Old example for v1.3.1 
        //public sealed class HttpContextReuse : IReuse
        //{
        //    public static readonly HttpContextReuse Instance = new HttpContextReuse();

        //    public static T GetOrAddToContext<T>(int factoryID, Func<T> factory)
        //    {
        //        var key = KEY_UNIQUE_PREFIX + factoryID;
        //        var items = HttpContext.Current.Items;
        //        lock (_locker)
        //            if (!items.Contains(key))
        //                items[key] = factory();
        //        return (T)items[key];
        //    }

        //    public Expression Of(Request request, IRegistry registry, int factoryID, Expression factoryExpr)
        //    {
        //        return Expression.Call(_getOrAddToContextMethod.MakeGenericMethod(factoryExpr.Type),
        //            Expression.Constant(factoryID),        // use factoryID (unique per Container) as service ID.
        //            Expression.Lambda(factoryExpr, null)); // pass Func<TService> to create service only when not found in context.
        //    }

        //    private static readonly object _locker = new object();
        //    private readonly MethodInfo _getOrAddToContextMethod = typeof(HttpContextReuse).GetMethod("GetOrAddToContext");
        //    private const string KEY_UNIQUE_PREFIX = "DryIocHCR#";
        //}

        #region CUT

        public class Soose
        {
            public int Scopes;

            public Soose(int scopes)
            {
                Scopes = scopes;
            }
        }

        public class ServiceWithResolutionAndSingletonDependencies
        {
            public SingletonDep SingletonDep { get; set; }
            public ResolutionScopeDep ResolutionScopeDep { get; set; }

            public ServiceWithResolutionAndSingletonDependencies(SingletonDep singletonDep, ResolutionScopeDep resolutionScopeDep)
            {
                SingletonDep = singletonDep;
                ResolutionScopeDep = resolutionScopeDep;
            }
        }

        public class ResolutionScopeDep { }

        public class SingletonDep
        {
            public ResolutionScopeDep ResolutionScopeDep { get; set; }

            public SingletonDep(ResolutionScopeDep resolutionScopeDep)
            {
                ResolutionScopeDep = resolutionScopeDep;
            }
        }

        #endregion
    }
}