﻿/*
The MIT License (MIT)

Copyright (c) 2013 Maksim Volkau

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DryIoc
{
    /// <summary>
    /// IoC Container. Documentation is available at https://bitbucket.org/dadhi/dryioc.
    /// TODO:
    /// - Add ToString to Factory, etc.
    /// - Speed up registration.
    /// </summary>
    public class Container : IRegistry, IDisposable
    {
        public static HashTree<Type, Factory> DefaultGenericWrappers = OpenGenericsSupport.GenericWrappers;

        public static Func<ResolutionRules> GetDefaultResolutionRules = () =>
        {
            var rules = new ResolutionRules();
            rules.ForUnregisteredService.Update(_ => OpenGenericsSupport.ResolveOpenGenericsAndEnumerable);
            return rules;
        };

        public Container(ResolutionRules resolutionRules = null, HashTree<Type, Factory> genericWrappers = null)
        {
            _factories = Ref.Of(HashTree<Type, object>.Empty);
            _decorators = Ref.Of(HashTree<Type, Factory[]>.Empty);
            _genericWrappers = Ref.Of(genericWrappers ?? DefaultGenericWrappers);
            _resolutionRules = resolutionRules ?? GetDefaultResolutionRules();

            _singletons = _reusedInScope = new Scope();

            _defaultResolutionCache = Ref.Of(HashTree<Type, CompiledFactory>.Empty);
            _keyedResolutionCache = Ref.Of(HashTree<Type, HashTree<object, CompiledFactory>>.Empty);

            _resolutionRoot = new ResolutionRoot();
        }

        public Container(Container source)
        {
            source.ThrowIfNull();

            _factories = source._factories;
            _decorators = source._decorators;
            _genericWrappers = source._genericWrappers;
            _resolutionRules = source._resolutionRules;

            _singletons = source._singletons;
            _reusedInScope = source._reusedInScope;

            _defaultResolutionCache = source._defaultResolutionCache;
            _keyedResolutionCache = source._keyedResolutionCache;

            _resolutionRoot = source._resolutionRoot;
        }

        public Container CreateScope()
        {
            return new Container(this) { _reusedInScope = new Scope() };
        }

        public Container CreateChildContainer()
        {
            var container = new Container(_resolutionRules, _genericWrappers.Value);
            container.ResolutionRules.ForUnregisteredService.Update(x => x.Append(
                (request, _) => ((IRegistry)this).GetOrAddFactory(request, IfUnresolved.ReturnNull)));
            return container;
        }

        public void Dispose()
        {
            _reusedInScope.Dispose();
        }

        public Request CreateRequest(Type serviceType, object serviceKey = null)
        {
            return new Request(_resolutionRoot, null, serviceType, serviceKey);
        }

        #region IRegistrator

        public void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            factory.ThrowIfNull().ThrowIfCannotBeRegisteredWithServiceType(serviceType.ThrowIfNull());
            switch (factory.Setup.Type)
            {
                case FactoryType.Decorator:
                    _decorators.Update(x => x.AddOrUpdate(serviceType, new[] { factory }, ArrayTools.Append));
                    break;
                case FactoryType.GenericWrapper:
                    _genericWrappers.Update(x => x.AddOrUpdate(serviceType, factory));
                    break;
                default:
                    AddFactory(factory, serviceType, serviceKey, ifAlreadyRegistered);
                    break;
            }
        }

        public bool IsRegistered(Type serviceType, string serviceName)
        {
            return GetFactoryOrDefault(serviceType.ThrowIfNull(), serviceName, ResolutionRules.ToGetSingleFactory,
                ifNotFoundLookForOpenGenericServiceType: true) != null;
        }

        #endregion

        #region IResolver

        object IResolver.ResolveDefault(Type serviceType, IfUnresolved ifUnresolved)
        {
            var compiledFactory = _defaultResolutionCache.Value.GetValueOrDefault(serviceType)
                ?? ResolveAndCacheFactory(serviceType, ifUnresolved);
            return compiledFactory(_resolutionRoot.Store.Value, _reusedInScope, resolutionScope: null);
        }

        object IResolver.ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved)
        {
            var entry = _keyedResolutionCache.Value.GetValueOrDefault(serviceType);
            if (entry != null)
            {
                var cachedFactory = entry.GetValueOrDefault(serviceKey);
                if (cachedFactory != null)
                    return cachedFactory(_resolutionRoot.Store.Value, _reusedInScope, resolutionScope: null);
            }

            var factory = ((IRegistry)this).GetOrAddFactory(CreateRequest(serviceType, serviceKey), ifUnresolved);
            if (factory == null)
                return null;

            var newFactory = factory.GetExpression().CompileToFactory();
            entry = (entry ?? HashTree<object, CompiledFactory>.Empty).AddOrUpdate(serviceKey, newFactory);
            _keyedResolutionCache.Update(x => x.AddOrUpdate(serviceType, entry));
            return newFactory(_resolutionRoot.Store.Value, _reusedInScope, resolutionScope: null);
        }

        private CompiledFactory ResolveAndCacheFactory(Type serviceType, IfUnresolved ifUnresolved)
        {
            var request = CreateRequest(serviceType);
            var factory = ((IRegistry)this).GetOrAddFactory(request, ifUnresolved);
            if (factory == null)
                return delegate { return null; };
            var newFactory = factory.GetExpression().CompileToFactory();
            _defaultResolutionCache.Update(x => x.AddOrUpdate(serviceType, newFactory));
            return newFactory;
        }

        #endregion

        #region IRegistry

        public ResolutionRules ResolutionRules { get { return _resolutionRules; } }

        RegistryWeakRef IRegistry.SelfWeakRef
        {
            get { return _selfWeakRef ?? (_selfWeakRef = new RegistryWeakRef(this)); }
        }

        Scope IRegistry.Singletons { get { return _singletons; } }
        Scope IRegistry.ReusedInScope { get { return _reusedInScope; } }

        FactoryWithContext IRegistry.GetOrAddFactory(Request request, IfUnresolved ifUnresolved)
        {
            var factory = GetFactoryOrDefault(request.ServiceType, request.ServiceKey, ResolutionRules.ToGetSingleFactory);
            if (factory != null && factory.ProvidesFactoryPerRequest)
                factory = factory.GetFactoryPerRequestOrDefault(request, this);

            if (factory != null)
                return factory.WithContext(request, this);

            var ruleFactoryContext = ResolutionRules.ForUnregisteredService.Invoke(rule => rule(request, this));
            if (ruleFactoryContext != null)
            {
                Register(ruleFactoryContext.Factory, request.ServiceType, request.ServiceKey, IfAlreadyRegistered.ThrowIfNamed);
                return ruleFactoryContext;
            }

            Throw.If(ifUnresolved == IfUnresolved.Throw, Error.UNABLE_TO_RESOLVE_SERVICE, request);
            return null;
        }

        Factory IRegistry.GetFactoryOrDefault(Type serviceType, object serviceKey)
        {
            return GetFactoryOrDefault(serviceType.ThrowIfNull(), serviceKey, ResolutionRules.ToGetSingleFactory,
                ifNotFoundLookForOpenGenericServiceType: true);
        }

        IEnumerable<KV<object, Factory>> IRegistry.GetAllFactories(Type serviceType)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && serviceType.IsGenericType && !serviceType.IsGenericTypeDefinition)
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericTypeDefinition());
            return entry == null ? Enumerable.Empty<KV<object, Factory>>()
                : entry is Factory ? new[] { new KV<object, Factory>(0, (Factory)entry) }
                : ((KeyedFactoriesEntry)entry).Factories.Enumerate();
        }

        Expression IRegistry.GetDecoratorExpressionOrDefault(Request request)
        {
            // Decorators for non service types are not supported.
            if (request.FactoryType != FactoryType.Service)
                return null;

            // We are already resolving decorator for the service, so stop now.
            var parent = request.GetNonWrapperParentOrDefault();
            if (parent != null && parent.DecoratedFactoryID == request.FactoryID)
                return null;

            var serviceType = request.ServiceType;
            var decoratorFuncType = typeof(Func<,>).MakeGenericType(serviceType, serviceType);

            var decorators = _decorators.Value;
            LambdaExpression resultFuncDecorator = null;
            var funcDecorators = decorators.GetValueOrDefault(decoratorFuncType);
            if (funcDecorators != null)
            {
                var decoratorRequest = request.MakeDecorated();
                for (var i = 0; i < funcDecorators.Length; i++)
                {
                    var decorator = funcDecorators[i];
                    if (((DecoratorSetup)decorator.Setup).IsApplicable(request))
                    {
                        var newDecorator = decorator.WithContext(decoratorRequest, this).GetExpression();
                        if (resultFuncDecorator == null)
                        {
                            var decorated = Expression.Parameter(serviceType, "decorated");
                            resultFuncDecorator = Expression.Lambda(Expression.Invoke(newDecorator, decorated), decorated);
                        }
                        else
                        {
                            var decorateDecorator = Expression.Invoke(newDecorator, resultFuncDecorator.Body);
                            resultFuncDecorator = Expression.Lambda(decorateDecorator, resultFuncDecorator.Parameters[0]);
                        }
                    }
                }
            }

            var serviceDecorators = decorators.GetValueOrDefault(serviceType);
            var openGenericDecoratorIndex = serviceDecorators == null ? 0 : serviceDecorators.Length;
            var openGenericServiceType = request.OpenGenericServiceType;
            if (openGenericServiceType != null)
                serviceDecorators = serviceDecorators.Append(decorators.GetValueOrDefault(openGenericServiceType));

            Expression resultDecorator = resultFuncDecorator;
            if (serviceDecorators != null)
            {
                var decoratorRequest = request.MakeDecorated();
                for (var i = 0; i < serviceDecorators.Length; i++)
                {
                    var decorator = serviceDecorators[i];
                    if (((DecoratorSetup)decorator.Setup).IsApplicable(request))
                    {
                        // Cache closed generic registration produced by open-generic decorator.
                        if (i >= openGenericDecoratorIndex && decorator.ProvidesFactoryPerRequest)
                        {
                            decorator = decorator.GetFactoryPerRequestOrDefault(request, this);
                            Register(decorator, serviceType, null, IfAlreadyRegistered.ThrowIfNamed);
                        }

                        var decoratorExpr = request.Root.GetCachedFactoryExpression(decorator.ID);
                        if (decoratorExpr == null)
                        {
                            IList<Type> unusedFunArgs;
                            var funcExpr = decorator.WithContext(decoratorRequest, this)
                                .GetFuncWithArgsOrDefault(decoratorFuncType, out unusedFunArgs)
                                .ThrowIfNull(Error.DECORATOR_FACTORY_SHOULD_SUPPORT_FUNC_RESOLUTION, decoratorFuncType);

                            decoratorExpr = unusedFunArgs != null ? funcExpr.Body : funcExpr;
                            request.Root.CacheFactoryExpression(decorator.ID, decoratorExpr);
                        }

                        if (resultDecorator == null || !(decoratorExpr is LambdaExpression))
                            resultDecorator = decoratorExpr;
                        else
                        {
                            if (!(resultDecorator is LambdaExpression))
                                resultDecorator = Expression.Invoke(decoratorExpr, resultDecorator);
                            else
                            {
                                var prevDecorators = ((LambdaExpression)resultDecorator);
                                var decorateDecorator = Expression.Invoke(decoratorExpr, prevDecorators.Body);
                                resultDecorator = Expression.Lambda(decorateDecorator, prevDecorators.Parameters[0]);
                            }
                        }
                    }
                }
            }

            return resultDecorator;
        }

        Factory IRegistry.GetGenericWrapperOrDefault(Type openGenericServiceType)
        {
            return _genericWrappers.Value.GetValueOrDefault(openGenericServiceType);
        }

        Type IRegistry.GetWrappedServiceTypeOrSelf(Type serviceType)
        {
            if (!serviceType.IsGenericType)
                return serviceType;

            var factory = _genericWrappers.Value.GetValueOrDefault(serviceType.GetGenericTypeDefinition());
            if (factory == null || factory.Setup.Type != FactoryType.GenericWrapper)
                return serviceType;

            var wrapperSetup = ((GenericWrapperSetup)factory.Setup);
            var wrappedType = wrapperSetup.GetWrappedServiceType(serviceType.GetGenericArguments());
            return ((IRegistry)this).GetWrappedServiceTypeOrSelf(wrappedType);
        }

        #endregion

        #region Factories Add/Get

        private sealed class KeyedFactoriesEntry
        {
            public readonly HashTree<object, Factory> Factories;
            public readonly int LatestIndex = -1;

            public KeyedFactoriesEntry(int latestIndex, HashTree<object, Factory> factories)
            {
                Factories = factories;
                LatestIndex = latestIndex;
            }
        }

        private void AddFactory(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            if (serviceKey == null)
            {
                _factories.Update(x => x.AddOrUpdate(serviceType, factory, (oldEntry, entry) =>
                {
                    if (oldEntry is Factory)
                        return ifAlreadyRegistered == IfAlreadyRegistered.KeepAlreadyRegistered
                            ? oldEntry
                            : new KeyedFactoriesEntry(1, HashTree<object, Factory>.Empty
                                .AddOrUpdate(0, (Factory)oldEntry)
                                .AddOrUpdate(1, (Factory)entry));

                    var oldFactories = ((KeyedFactoriesEntry)oldEntry);
                    var latestIndex = oldFactories.LatestIndex + 1;
                    if (latestIndex > 0 && ifAlreadyRegistered == IfAlreadyRegistered.KeepAlreadyRegistered)
                        return oldEntry;
                    return new KeyedFactoriesEntry(latestIndex,
                        oldFactories.Factories.AddOrUpdate(latestIndex, (Factory)entry));
                }));
            }
            else if (serviceKey is string)
            {
                var newEntry = new KeyedFactoriesEntry(-1, HashTree<object, Factory>.Empty.AddOrUpdate(serviceKey, factory));
                _factories.Update(x => x.AddOrUpdate(serviceType, newEntry, (oldEntry, entry) =>
                {
                    if (oldEntry is Factory)
                        return new KeyedFactoriesEntry(0,
                            ((KeyedFactoriesEntry)entry).Factories.AddOrUpdate(0, (Factory)oldEntry));

                    var oldFactories = ((KeyedFactoriesEntry)oldEntry);
                    return new KeyedFactoriesEntry(oldFactories.LatestIndex,
                        oldFactories.Factories.AddOrUpdate(serviceKey, factory, (oldFactory, _) =>
                        {
                            if (ifAlreadyRegistered == IfAlreadyRegistered.KeepAlreadyRegistered)
                                return oldFactory;
                            throw Error.DUPLICATE_SERVICE_NAME.Of(serviceType, serviceKey, oldFactory);
                        }));
                }));
            }
            else if (serviceKey is int && ((int)serviceKey) >= 0)
            {
                var index = (int)serviceKey;
                var newEntry = new KeyedFactoriesEntry(index, HashTree<object, Factory>.Empty.AddOrUpdate(index, factory));
                _factories.Update(x => x.AddOrUpdate(serviceType, newEntry, (oldEntry, entry) =>
                {
                    if (oldEntry is Factory)
                    {
                        if (index == 0)
                        {
                            if (ifAlreadyRegistered == IfAlreadyRegistered.KeepAlreadyRegistered)
                                return oldEntry;
                            throw Error.DUPLICATE_SERVICE_INDEX.Of(serviceType, index, oldEntry);
                        }
                        return new KeyedFactoriesEntry(index,
                            ((KeyedFactoriesEntry)entry).Factories.AddOrUpdate(0, (Factory)oldEntry));
                    }

                    var oldFactories = ((KeyedFactoriesEntry)oldEntry);
                    return new KeyedFactoriesEntry(index > oldFactories.LatestIndex ? index : oldFactories.LatestIndex,
                        oldFactories.Factories.AddOrUpdate(index, factory, (oldFactory, _) =>
                        {
                            if (ifAlreadyRegistered == IfAlreadyRegistered.KeepAlreadyRegistered)
                                return oldFactory;
                            throw Error.DUPLICATE_SERVICE_INDEX.Of(serviceType, index, oldFactory);
                        }));
                }));
            }
            else throw Error.UNABLE_TO_REGISTER_WITH_NON_INT_OR_STRING_SERVICE_KEY.Of(serviceType, serviceKey);
        }

        private Factory GetFactoryOrDefault(
            Type serviceType, object serviceKey, ResolutionRules.GetSingleFactory getSingleFactory,
            bool ifNotFoundLookForOpenGenericServiceType = false)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && ifNotFoundLookForOpenGenericServiceType &&
                serviceType.IsGenericType && !serviceType.IsGenericTypeDefinition)
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericTypeDefinition());

            if (entry != null)
            {
                if (entry is Factory)
                    return serviceKey == null || (serviceKey is int && (int)serviceKey == 0)
                        ? (Factory)entry
                        : null;

                var factories = ((KeyedFactoriesEntry)entry).Factories;
                if (serviceKey != null)
                    return factories.GetValueOrDefault(serviceKey);

                var indexed = factories.Enumerate().Where(x => x.Key is int).Select(x => x.Value).ToArray();
                if (indexed.Length == 1)
                    return indexed[0];

                if (indexed.Length > 1)
                    return getSingleFactory
                        .ThrowIfNull(Error.EXPECTED_SINGLE_DEFAULT_FACTORY, serviceType, indexed)
                        .Invoke(indexed);
            }

            return null;
        }

        #endregion

        #region Internal State

        private RegistryWeakRef _selfWeakRef;
        private readonly ResolutionRules _resolutionRules;

        private readonly Ref<HashTree<Type, object>> _factories; // where object is Factory or KeyedFactoriesEntry
        private readonly Ref<HashTree<Type, Factory[]>> _decorators;
        private readonly Ref<HashTree<Type, Factory>> _genericWrappers;

        private readonly Scope _singletons;
        private Scope _reusedInScope;

        private readonly Ref<HashTree<Type, CompiledFactory>> _defaultResolutionCache;
        private readonly Ref<HashTree<Type, HashTree<object, CompiledFactory>>> _keyedResolutionCache;

        private readonly ResolutionRoot _resolutionRoot;

        #endregion
    }

    public sealed class ResolutionRoot
    {
        public static readonly ParameterExpression StoreParameter = Expression.Parameter(typeof(AppendStore<object>), "store");
        public static readonly ParameterExpression ReusedInScopeParameter = Expression.Parameter(typeof(Scope), "reusedInScope");
        public static readonly ParameterExpression ReusedInResolutionParameter = Expression.Parameter(typeof(Scope), "reusedHere");

        public readonly Ref<AppendStore<object>> Store = Ref.Of(AppendStore<object>.Empty);
        public HashTree<int, Expression> FactoryExprCache = HashTree<int, Expression>.Empty;

        public Expression GetItemExpression(object item, Type itemType)
        {
            var itemIndex = -1;
            Store.Update(x =>
            {
                itemIndex = x.IndexOf(item);
                if (itemIndex == -1)
                    itemIndex = (x = x.Append(item)).Count - 1;
                return x;
            });

            var itemIndexExpr = Expression.Constant(itemIndex.ThrowIf(itemIndex == -1), typeof(int));
            var itemExpr = Expression.Call(StoreParameter, _storeGetMethod, itemIndexExpr);
            return Expression.Convert(itemExpr, itemType);
        }

        public Expression GetItemExpression<T>(T item)
        {
            return GetItemExpression(item, typeof(T));
        }

        public Expression GetRegistryItemExpression(IRegistry registry)
        {
            return Expression.Property(GetItemExpression(registry.SelfWeakRef), "Target");
        }

        public Expression GetCachedFactoryExpression(int factoryID)
        {
            return FactoryExprCache.GetFirstValueOfHashOrDefault(factoryID);
        }

        public void CacheFactoryExpression(int factoryID, Expression result)
        {
            Interlocked.Exchange(ref FactoryExprCache, FactoryExprCache.AddOrUpdate(factoryID, result));
        }

        private static readonly MethodInfo _storeGetMethod = typeof(AppendStore<object>).GetMethod("Get");
    }

    public sealed class RegistryWeakRef
    {
        public RegistryWeakRef(IRegistry registry)
        {
            _weakRef = new WeakReference(registry);
        }

        public IRegistry Target
        {
            get { return (_weakRef.Target as IRegistry).ThrowIfNull(Error.CONTAINER_IS_GARBAGE_COLLECTED); }
        }

        private readonly WeakReference _weakRef;
    }

    public sealed class AppendStore<T>
    {
        public static readonly AppendStore<T> Empty = new AppendStore<T>(0, HashTree<int, T[]>.Empty);

        public readonly int Count;

        public AppendStore<T> Append(T value)
        {
            return new AppendStore<T>(Count + 1,
                _tree.AddOrUpdate(Count >> NODE_ARRAY_BIT_COUNT, new[] { value }, ArrayTools.Append));
        }

        public int IndexOf(object value, int defaultIndex = -1)
        {
            foreach (var node in _tree.Enumerate())
            {
                var indexInNode = node.Value.IndexOf(x => ReferenceEquals(x, value) || Equals(x, value));
                if (indexInNode != -1)
                    return node.Key << NODE_ARRAY_BIT_COUNT | indexInNode;
            }

            return defaultIndex;
        }

        public object Get(int index)
        {
            return index <= NODE_ARRAY_BITS ? _tree.Value[index]
                : _tree.GetFirstValueOfHashOrDefault(index >> NODE_ARRAY_BIT_COUNT)[index & NODE_ARRAY_BITS];
        }

        #region Implementation

        private const int NODE_ARRAY_BITS = 31;     // (11111 binary). So the array would be size of 32. Make it 15 (1111) and BIT_COUNT=4 for array of size 16
        private const int NODE_ARRAY_BIT_COUNT = 5; // number of bits in NODE_ARRAY_BITS.

        private readonly HashTree<int, T[]> _tree;

        private AppendStore(int count, HashTree<int, T[]> tree)
        {
            Count = count;
            _tree = tree;
        }

        #endregion
    }

    public delegate object CompiledFactory(AppendStore<object> resolutionStore, Scope currentScope, Scope resolutionScope);

    public static partial class FactoryCompiler
    {
        public static Expression<CompiledFactory> ToCompiledFactoryExpression(this Expression expression)
        {
            // Removing not required Convert from expression root, because CompiledFactory result still be converted at the end.
            if (expression.NodeType == ExpressionType.Convert)
                expression = ((UnaryExpression)expression).Operand;
            return Expression.Lambda<CompiledFactory>(expression,
                ResolutionRoot.StoreParameter, ResolutionRoot.ReusedInScopeParameter, ResolutionRoot.ReusedInResolutionParameter);
        }

        public static CompiledFactory CompileToFactory(this Expression expression)
        {
            var factoryExpression = expression.ToCompiledFactoryExpression();
            CompiledFactory factory = null;
            CompileToMethod(factoryExpression, ref factory);
            // ReSharper disable ConstantNullCoalescingCondition
            return factory ?? factoryExpression.Compile();
            // ReSharper restore ConstantNullCoalescingCondition
        }

        // Partial method definition to be implemented in .NET40 version of Container.
        // It is optional and fine to be not implemented.
        static partial void CompileToMethod(Expression<CompiledFactory> factoryExpr, ref CompiledFactory resultFactory);
    }

    public static class OpenGenericsSupport
    {
        public static Type[] FuncTypes = { typeof(Func<>), typeof(Func<,>), typeof(Func<,,>), typeof(Func<,,,>), typeof(Func<,,,,>) };

        public static ResolutionRules.ResolveUnregisteredService[] ResolveOpenGenericsAndEnumerable;

        public static HashTree<Type, Factory> GenericWrappers;

        static OpenGenericsSupport()
        {
            ResolveOpenGenericsAndEnumerable = new[]
            {
                ResolveOpenGeneric,
                ResolveEnumerableAsStaticArray
            };

            GenericWrappers = HashTree<Type, Factory>.Empty;
            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(Many<>),
                new FactoryProvider(
                    (_, __) => new DelegateFactory(GetManyExpression),
                    GenericWrapperSetup.Default));

            var funcFactory = new FactoryProvider(
                (_, __) => new DelegateFactory(GetFuncExpression),
                GenericWrapperSetup.With(t => t[t.Length - 1]));
            foreach (var funcType in FuncTypes)
                GenericWrappers = GenericWrappers.AddOrUpdate(funcType, funcFactory);

            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(Lazy<>),
                new ReflectionFactory(typeof(Lazy<>),
                    getConstructor: t => t.GetConstructor(new[] { typeof(Func<>).MakeGenericType(t.GetGenericArguments()) }),
                    setup: GenericWrapperSetup.Default));

            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(Meta<,>),
                new FactoryProvider(GetMetaFactoryOrDefault, GenericWrapperSetup.With(t => t[0])));

            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(DebugExpression<>),
                new FactoryProvider((_, __) => new DelegateFactory(GetDebugExpression), GenericWrapperSetup.Default));
        }

        public static ResolutionRules.ResolveUnregisteredService ResolveOpenGeneric = (request, registry) =>
        {
            var openGenericServiceType = request.OpenGenericServiceType;
            if (openGenericServiceType == null)
                return null;

            var factory = registry.GetFactoryOrDefault(openGenericServiceType, request.ServiceKey)
                ?? registry.GetGenericWrapperOrDefault(openGenericServiceType);

            if (factory != null && factory.ProvidesFactoryPerRequest)
                factory = factory.GetFactoryPerRequestOrDefault(request, registry);

            return factory == null ? null : factory.WithContext(request, registry);
        };

        public static ResolutionRules.ResolveUnregisteredService ResolveEnumerableAsStaticArray = (request, registry) =>
        {
            if (!request.ServiceType.IsArray && request.OpenGenericServiceType != typeof(IEnumerable<>))
                return null;

            return new DelegateFactory(
                setup: GenericWrapperSetup.Default,
                getExpression: (req, reg) =>
                {
                    var collectionType = req.ServiceType;

                    var itemType = collectionType.IsArray
                        ? collectionType.GetElementType()
                        : collectionType.GetGenericArguments()[0];

                    var wrappedItemType = reg.GetWrappedServiceTypeOrSelf(itemType);

                    // Composite pattern support: filter out composite root from available keys.
                    var items = reg.GetAllFactories(wrappedItemType);
                    var parent = req.GetNonWrapperParentOrDefault();
                    if (parent != null && parent.ServiceType == wrappedItemType)
                    {
                        var parentFactoryID = parent.ResolvedFactory.ID;
                        items = items.Where(x => x.Value.ID != parentFactoryID);
                    }

                    var itemArray = items.ToArray();
                    Throw.If(itemArray.Length == 0, Error.UNABLE_TO_FIND_REGISTERED_ENUMERABLE_ITEMS, wrappedItemType,
                        req);

                    var itemExpressions = new List<Expression>(itemArray.Length);
                    for (var i = 0; i < itemArray.Length; i++)
                    {
                        var item = itemArray[i];
                        var itemRequest = req.Push(itemType, item.Key);
                        var itemFactory = reg.GetOrAddFactory(itemRequest, IfUnresolved.ReturnNull);
                        if (itemFactory != null)
                            itemExpressions.Add(itemFactory.GetExpression());
                    }

                    Throw.If(itemExpressions.Count == 0, Error.UNABLE_TO_RESOLVE_ENUMERABLE_ITEMS, itemType, req);
                    var newArrayExpr = Expression.NewArrayInit(itemType.ThrowIfNull(), itemExpressions);
                    return newArrayExpr;
                }).WithContext(request, registry);
        };

        public static Expression GetManyExpression(Request request, IRegistry registry)
        {
            var dynamicEnumerableType = request.ServiceType;
            var itemType = dynamicEnumerableType.GetGenericArguments()[0];

            var wrappedItemType = registry.GetWrappedServiceTypeOrSelf(itemType);

            // Composite pattern support: filter out composite root from available keys.
            var parentFactoryID = 0;
            var parent = request.GetNonWrapperParentOrDefault();
            if (parent != null && parent.ServiceType == wrappedItemType)
                parentFactoryID = parent.ResolvedFactory.ID;

            var resolveMethod = _resolveManyDynamicallyMethod.MakeGenericMethod(itemType, wrappedItemType);

            var registryRefExpr = request.Root.GetItemExpression(registry.SelfWeakRef);
            var resolveCallExpr = Expression.Call(resolveMethod, registryRefExpr, Expression.Constant(parentFactoryID));

            return Expression.New(dynamicEnumerableType.GetConstructors()[0], resolveCallExpr);
        }

        public static Expression GetFuncExpression(Request request, IRegistry registry)
        {
            var funcType = request.ServiceType;
            var funcTypeArgs = funcType.GetGenericArguments();
            var serviceType = funcTypeArgs[funcTypeArgs.Length - 1];

            var serviceRequest = request.PushPreservingParentKey(serviceType);
            var serviceFactory = registry.GetOrAddFactory(serviceRequest, IfUnresolved.Throw);

            if (funcTypeArgs.Length == 1)
                return Expression.Lambda(funcType, serviceFactory.GetExpression(), null);

            IList<Type> unusedFuncArgs;
            var funcExpr = serviceFactory.GetFuncWithArgsOrDefault(funcType, out unusedFuncArgs)
                .ThrowIfNull(Error.UNSUPPORTED_FUNC_WITH_ARGS, funcType, serviceRequest)
                .ThrowIf(unusedFuncArgs != null, Error.SOME_FUNC_PARAMS_ARE_UNUSED, unusedFuncArgs, request);
            return funcExpr;
        }

        public static Expression GetDebugExpression(Request request, IRegistry registry)
        {
            var ctor = request.ServiceType.GetConstructors()[0];
            var serviceType = request.ServiceType.GetGenericArguments()[0];
            var serviceRequest = request.PushPreservingParentKey(serviceType);
            var factory = registry.GetOrAddFactory(serviceRequest, IfUnresolved.Throw);
            var factoryExpr = factory.GetExpression().ToCompiledFactoryExpression();
            return Expression.New(ctor, request.Root.GetItemExpression(factoryExpr));
        }

        public static Factory GetMetaFactoryOrDefault(Request request, IRegistry registry)
        {
            var genericArgs = request.ServiceType.GetGenericArguments();
            var serviceType = genericArgs[0];
            var metadataType = genericArgs[1];

            var wrappedServiceType = registry.GetWrappedServiceTypeOrSelf(serviceType);
            object resultMetadata = null;
            var serviceKey = request.ServiceKey;
            if (serviceKey == null)
            {
                var result = registry.GetAllFactories(wrappedServiceType).FirstOrDefault(kv =>
                    kv.Value.Setup.Metadata != null && metadataType.IsInstanceOfType(kv.Value.Setup.Metadata));
                if (result != null)
                {
                    serviceKey = result.Key;
                    resultMetadata = result.Value.Setup.Metadata;
                }
            }
            else
            {
                var factory = registry.GetFactoryOrDefault(wrappedServiceType, serviceKey);
                if (factory != null)
                {
                    var metadata = factory.Setup.Metadata;
                    resultMetadata = metadata != null && metadataType.IsInstanceOfType(metadata) ? metadata : null;
                }
            }

            if (resultMetadata == null)
                return null;

            return new DelegateFactory((req, _) =>
            {
                var serviceRequest = req.Push(serviceType, serviceKey);
                var serviceExpr = registry.GetOrAddFactory(serviceRequest, IfUnresolved.Throw).GetExpression();
                var metaCtor = req.ServiceType.GetConstructors()[0];
                var metadataExpr = req.Root.GetItemExpression(resultMetadata, metadataType);
                return Expression.New(metaCtor, serviceExpr, metadataExpr);
            });
        }

        #region Implementation

        private static readonly MethodInfo _resolveManyDynamicallyMethod =
            typeof(OpenGenericsSupport).GetMethod("ResolveManyDynamically", BindingFlags.Static | BindingFlags.NonPublic);

        internal static IEnumerable<TService> ResolveManyDynamically<TService, TWrappedService>(
            RegistryWeakRef registryWeakRef, int parentFactoryID)
        {
            var itemType = typeof(TService);
            var wrappedItemType = typeof(TWrappedService);
            var registry = registryWeakRef.Target;

            var items = registry.GetAllFactories(wrappedItemType);
            if (parentFactoryID != -1)
                items = items.Where(kv => kv.Value.ID != parentFactoryID);

            foreach (var item in items)
            {
                var service = registry.ResolveKeyed(itemType, item.Key, IfUnresolved.ReturnNull);
                if (service != null) // skip unresolved items
                    yield return (TService)service;
            }
        }

        #endregion
    }

    public sealed class ResolutionRules
    {
        public delegate Factory GetSingleFactory(IEnumerable<Factory> factories);
        public GetSingleFactory ToGetSingleFactory = null;

        public delegate FactoryWithContext ResolveUnregisteredService(Request request, IRegistry registry);
        public Rules<ResolveUnregisteredService> ForUnregisteredService = new Rules<ResolveUnregisteredService>();

        public delegate object ResolveConstructorParameterServiceKey(ParameterInfo parameter, Request parent, IRegistry registry);
        public Rules<ResolveConstructorParameterServiceKey> ForConstructorParameter = new Rules<ResolveConstructorParameterServiceKey>();

        public delegate bool ResolvePropertyOrFieldServiceKey(out object key, MemberInfo member, Request parent, IRegistry registry);
        public Rules<ResolvePropertyOrFieldServiceKey> ForPropertyOrField = new Rules<ResolvePropertyOrFieldServiceKey>();

        public sealed class Rules<TRule>
        {
            public Rules(TRule[] items = null)
            {
                _items = items;
            }

            public bool IsEmpty
            {
                get
                {
                    var list = _items;
                    return list == null || list.Length == 0;
                }
            }

            public IEnumerable<TRule> Items
            {
                get { return _items ?? Enumerable.Empty<TRule>(); }
            }

            public TResult Invoke<TResult>(Func<TRule, TResult> invoke)
            {
                var result = default(TResult);
                var items = _items;
                if (items != null)
                    for (var i = 0; i < items.Length && Equals(result, default(TResult)); i++)
                        result = invoke(items[i]);
                return result;
            }

            public TRule[] Update(Func<TRule[], TRule[]> update)
            {
                var items = _items;
                _items = update(items == null ? null : items.ToArray());
                return items;
            }

            public TRule Append(TRule rule)
            {
                Update(rules => rules.Append(rule));
                return rule;
            }

            public void Remove(TRule rule)
            {
                Update(rules => rules.Remove(rule));
            }

            private TRule[] _items;
        }
    }

    public static class Error
    {
        public static readonly string UNABLE_TO_RESOLVE_SERVICE =
            "Unable to resolve service {0}\n. Please register service OR adjust resolution rules.";

        public static readonly string UNSUPPORTED_FUNC_WITH_ARGS =
            "Unsupported resolution as {0} of {1}.";

        public static readonly string EXPECTED_IMPL_TYPE_ASSIGNABLE_TO_SERVICE_TYPE =
            "Expecting implementation type {0} to be assignable to service type {1} but it is not.";

        public static readonly string UNABLE_TO_REGISTER_WITH_NON_INT_OR_STRING_SERVICE_KEY =
            "Unable to register service {0} with key that is not null, nor string, nor integer index, but {1}.";

        public static readonly string UNABLE_TO_REGISTER_NON_FACTORY_PROVIDER_FOR_OPEN_GENERIC_SERVICE =
            "Unable to register not a factory provider for open-generic service {0}.";

        public static readonly string UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE =
            "Unable to register open-generic implementation {0} with non-generic service {1}.";

        public static readonly string UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_CAUSE_SERVICE_DOES_NOT_SPECIFY_ALL_TYPE_ARGS =
            "Unable to register open-generic implementation {0} because service {1} should specify all of its type arguments, but specifies only {2}.";

        public static readonly string USUPPORTED_REGISTRATION_OF_NON_GENERIC_IMPL_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS =
            "Unsupported registration of implementation {0} which is not a generic type definition but contains generic parameters.\n" +
            "Consider to register generic type definition {1} instead.";

        public static readonly string USUPPORTED_REGISTRATION_OF_NON_GENERIC_SERVICE_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS =
            "Unsupported registration of service {0} which is not a generic type definition but contains generic parameters.\n" +
            "Consider to register generic type definition {1} instead.";

        public static readonly string EXPECTED_SINGLE_DEFAULT_FACTORY =
            "Expecting single default registration of {0} but found many:\n{1}.";

        public static readonly string EXPECTED_NON_ABSTRACT_IMPL_TYPE =
            "Expecting not abstract and not interface implementation type, but found {0}.";

        public static readonly string NO_PUBLIC_CONSTRUCTOR_DEFINED =
            "There is no public constructor defined for {0}.";

        public static readonly string CONSTRUCTOR_MISSES_SOME_PARAMETERS =
            "Constructor [{0}] of {1} misses some arguments required for {2} dependency.";

        public static readonly string UNABLE_TO_SELECT_CONSTRUCTOR =
            "Unable to select single constructor from {0} available in {1}.\n" +
            "Please provide constructor selector when registering service.";

        public static readonly string EXPECTED_FUNC_WITH_MULTIPLE_ARGS =
            "Expecting Func with one or more arguments but found {0}.";

        public static readonly string EXPECTED_CLOSED_GENERIC_SERVICE_TYPE =
            "Expecting closed-generic service type but found {0}.";

        public static readonly string RECURSIVE_DEPENDENCY_DETECTED =
            "Recursive dependency is detected in resolution of:\n{0}.";

        public static readonly string SCOPE_IS_DISPOSED =
            "Scope is disposed and scoped instances are no longer available.";

        public static readonly string CONTAINER_IS_GARBAGE_COLLECTED =
            "Container is no longer available (has been garbage-collected).";

        public static readonly string DUPLICATE_SERVICE_NAME =
            "Service {0} with the same name '{1}' is already registered with {2}.";

        public static readonly string DUPLICATE_SERVICE_INDEX =
            "Service {0} with the same index '{1}' is already registered with {2}.";

        public static readonly string GENERIC_WRAPPER_EXPECTS_SINGLE_TYPE_ARG_BY_DEFAULT =
            "Generic Wrapper is working with single service type only, but found many:\n{0}.\n" +
            "Please specify service type selector in Generic Wrapper setup upon registration.";

        public static readonly string SOME_FUNC_PARAMS_ARE_UNUSED =
            "Found some unused Func parameters:\n{0}\nwhen resolving {1}.";

        public static readonly string DECORATOR_FACTORY_SHOULD_SUPPORT_FUNC_RESOLUTION =
            "Decorator factory should support resolution as {0}, but it does not.";

        public static readonly string UNABLE_TO_FIND_REGISTERED_ENUMERABLE_ITEMS =
            "Unable to find registered services of item type (unwrapped) {0} when resolving {1}.";

        public static readonly string UNABLE_TO_RESOLVE_ENUMERABLE_ITEMS =
            "Unable to resolve any service of item type {0} when resolving {1}.";

        public static readonly string DELEGATE_FACTORY_EXPRESSION_RETURNED_NULL =
            "Delegate factory expression returned NULL when resolving {0}.";

        public static readonly string UNABLE_TO_MATCH_IMPL_BASE_TYPES_WITH_SERVICE_TYPE =
            "Unable to match service with any of open-generic implementation {0} implemented types {1} when resolving {2}.";

        public static readonly string UNABLE_TO_FIND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE =
            "Unable to find for open-generic implementation {0} the type argument {1} when resolving {2}.";
    }

    public static class Registrator
    {
        /// <summary>
        /// Registers service <paramref name="serviceType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">Optional name of the service.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType, Factory factory,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service of <typeparamref name="TService"/>.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">Optional name of the service.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService>(this IRegistrator registrator, Factory factory,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service <paramref name="serviceType"/> with corresponding <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="getConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType,
            Type implementationType, IReuse reuse = null, GetConstructor getConstructor = null, FactorySetup setup = null,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            var factory = new ReflectionFactory(implementationType, reuse, getConstructor, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service of <paramref name="implementationType"/>. ServiceType will be the same as <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator,
            Type implementationType, IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            var factory = new ReflectionFactory(implementationType, reuse, withConstructor, setup);
            registrator.Register(factory, implementationType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service of <typeparamref name="TService"/> type implemented by <typeparamref name="TImplementation"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService, TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
            where TImplementation : TService
        {
            var factory = new ReflectionFactory(typeof(TImplementation), reuse, withConstructor, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers implementation type <typeparamref name="TImplementation"/> with itself as service type.
        /// </summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            var factory = new ReflectionFactory(typeof(TImplementation), reuse, withConstructor, setup);
            registrator.Register(factory, typeof(TImplementation), named, ifAlreadyRegistered);
        }

        public static Func<Type, bool> PublicTypes =
            type => (type.IsPublic || type.IsNestedPublic) && type != typeof(object);

        /// <summary>
        /// Registers single registration for all implemented public interfaces and base classes.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Service implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="types">Optional condition to include selected types only. Default value is <see cref="PublicTypes"/></param>
        /// <param name="named">Optional registration name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll(this IRegistrator registrator,
            Type implementationType, IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            Func<Type, bool> types = null, string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            var registration = new ReflectionFactory(implementationType, reuse, withConstructor, setup);

            var implementedTypes = implementationType.GetImplementedTypes(TypeTools.IncludeItself.AsFirst);
            var implementedServiceTypes = implementedTypes.Where(types ?? PublicTypes);
            if (implementationType.IsGenericTypeDefinition)
            {
                var implTypeArgs = implementationType.GetGenericArguments();
                implementedServiceTypes = implementedServiceTypes
                    .Where(t => t.IsGenericType && t.ContainsGenericParameters && t.ContainsAllGenericParameters(implTypeArgs))
                    .Select(t => t.GetGenericTypeDefinition());
            }

            var atLeastOneRegistered = false;
            foreach (var serviceType in implementedServiceTypes)
            {
                registrator.Register(registration, serviceType, named, ifAlreadyRegistered);
                atLeastOneRegistered = true;
            }

            Throw.If(!atLeastOneRegistered, "Unable to register any of implementation {0} implemented services {1}.",
                implementationType, implementedTypes);
        }

        /// <summary>
        /// Registers single registration for all implemented public interfaces and base classes.
        /// </summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            registrator.RegisterAll(typeof(TImplementation), reuse, withConstructor, setup, null, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers a factory delegate for creating an instance of <typeparamref name="TService"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code>
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="lambda">The delegate used to create a instance of <typeparamref name="TService"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterDelegate<TService>(this IRegistrator registrator,
            Func<IResolver, TService> lambda, IReuse reuse = null, FactorySetup setup = null,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            var factory = new DelegateFactory((request, registry) =>
                Expression.Invoke(request.Root.GetItemExpression(lambda), request.Root.GetRegistryItemExpression(registry)),
                reuse, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers a pre-created service instance of <typeparamref name="TService"/> 
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">The pre-created instance of <typeparamref name="TService"/>.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service name.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterInstance<TService>(this IRegistrator registrator,
            TService instance, FactorySetup setup = null,
            string named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfNamed)
        {
            registrator.RegisterDelegate(_ => instance, Reuse.Transient, setup, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Returns true if <paramref name="serviceType"/> is registered in container or its open generic definition is registered in container.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The type of the registered service.</param>
        /// <param name="named">Optional service name</param>
        /// <returns>True if <paramref name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered(this IRegistrator registrator, Type serviceType, string named = null)
        {
            return registrator.IsRegistered(serviceType, named);
        }

        /// <summary>
        /// Returns true if <typeparamref name="TService"/> type is registered in container or its open generic definition is registered in container.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="named">Optional service name</param>
        /// <returns>True if <typeparamref name="TService"/> name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered<TService>(this IRegistrator registrator, string named = null)
        {
            return registrator.IsRegistered(typeof(TService), named);
        }
    }

    public static class Resolver
    {
        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return resolver.ResolveDefault(serviceType, ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.ResolveDefault(typeof(TService), ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceName">Service name.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, string serviceName, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return serviceName == null
                ? resolver.ResolveDefault(serviceType, ifUnresolved)
                : resolver.ResolveKeyed(serviceType, serviceName, ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceName">Service name.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, string serviceName, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.Resolve(typeof(TService), serviceName, ifUnresolved);
        }

        public static readonly BindingFlags MembersToResolve = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>
        /// For given instance resolves and sets non-initialized (null) properties from container.
        /// It does not throw if property is not resolved, so you might need to check property value afterwards.
        /// </summary>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">Service instance with properties to resolve and initialize.</param>
        /// <param name="getServiceName">Optional function to find service name, otherwise service name will be null.</param>
        public static void ResolvePropertiesAndFields(this IResolver resolver, object instance, Func<MemberInfo, string> getServiceName = null)
        {
            var implType = instance.ThrowIfNull().GetType();
            getServiceName = getServiceName ?? (_ => null);

            foreach (var property in implType.GetProperties(MembersToResolve).Where(p => p.GetSetMethod() != null))
            {
                var value = resolver.Resolve(property.PropertyType, getServiceName(property), IfUnresolved.ReturnNull);
                if (value != null)
                    property.SetValue(instance, value, null);
            }

            foreach (var field in implType.GetFields(MembersToResolve).Where(f => !f.IsInitOnly))
            {
                var value = resolver.Resolve(field.FieldType, getServiceName(field), IfUnresolved.ReturnNull);
                if (value != null)
                    field.SetValue(instance, value);
            }
        }
    }

    public sealed class Request
    {
        public readonly ResolutionRoot Root;

        public readonly Request Parent; // can be null for resolution root
        public readonly Type ServiceType;
        public readonly object ServiceKey; // null for default, string for named or integer index for multiple defaults.

        public readonly DependencyInfo Dependency;
        public readonly int DecoratedFactoryID;

        public Factory ResolvedFactory;

        public int FactoryID { get { return ResolvedFactory.ID; } }
        public Type ImplementationType { get { return ResolvedFactory.ImplementationType; } }
        public FactoryType FactoryType { get { return ResolvedFactory.Setup.Type; } }
        public object Metadata { get { return ResolvedFactory.Setup.Metadata; } }

        public Type OpenGenericServiceType
        {
            get { return ServiceType.IsGenericType ? ServiceType.GetGenericTypeDefinition() : null; }
        }

        public Request Push(Type serviceType, object serviceKey, DependencyInfo dependency = null)
        {
            return new Request(Root, this, serviceType, serviceKey, dependency);
        }

        public Request PushPreservingParentKey(Type serviceType, DependencyInfo dependency = null)
        {
            return new Request(Root, this, serviceType, ServiceKey, dependency);
        }

        public Request ResolveWith(Factory factory)
        {
            for (var p = Parent; p != null; p = p.Parent)
                Throw.If(p.ResolvedFactory != null && p.ResolvedFactory.ID == factory.ID && p.ResolvedFactory.Setup.Type == FactoryType.Service,
                    Error.RECURSIVE_DEPENDENCY_DETECTED, this);

            return new Request(Root, Parent, ServiceType, ServiceKey, Dependency, DecoratedFactoryID, factory);
        }

        public Request MakeDecorated()
        {
            return new Request(Root, Parent, ServiceType, ServiceKey, Dependency, decoratedFactoryID: FactoryID, factory: ResolvedFactory);
        }

        public Request GetNonWrapperParentOrDefault()
        {
            var p = Parent;
            while (p != null && p.ResolvedFactory.Setup.Type == FactoryType.GenericWrapper)
                p = p.Parent;
            return p;
        }

        public IEnumerable<Request> Enumerate()
        {
            for (var x = this; x != null; x = x.Parent)
                yield return x;
        }

        public override string ToString()
        {
            var message = new StringBuilder().Append(Print());
            return Parent == null ? message.ToString()
                 : Parent.Enumerate().Aggregate(message,
                    (m, r) => m.AppendLine().Append(" in ").Append(r.Print())).ToString();
        }

        #region Implementation

        internal Request(ResolutionRoot root, Request parent, Type serviceType, object serviceKey,
            DependencyInfo dependency = null, int decoratedFactoryID = 0, Factory factory = null)
        {
            Root = root;

            Parent = parent;

            ServiceType = serviceType.ThrowIfNull()
                .ThrowIf(serviceType.IsGenericTypeDefinition, Error.EXPECTED_CLOSED_GENERIC_SERVICE_TYPE, serviceType);

            ServiceKey = serviceKey;
            Dependency = dependency;

            if (factory != null)
                ResolvedFactory = factory;

            DecoratedFactoryID = decoratedFactoryID;
        }

        // example: "unnamed generic-wrapper DryIoc.UnitTests.IService : DryIoc.UnitTests.Service (CtorParam service)"
        private string Print()
        {
            var message = ServiceKey is string ? "\"" + ServiceKey + "\""
                : ServiceKey is int ? "#" + ServiceKey
                : "unnamed";

            if (ResolvedFactory != null)
            {
                var kind = FactoryType == FactoryType.Decorator
                ? " decorator" : FactoryType == FactoryType.GenericWrapper
                ? " generic-wrapper" : string.Empty;

                var type = ImplementationType != null && ImplementationType != ServiceType
                    ? ImplementationType.Print() + " : " + ServiceType.Print()
                    : ServiceType.Print();

                message += kind + " " + type;
            }

            var dependency = Dependency == null ? string.Empty : " (" + Dependency + ")";
            return message + dependency;
        }

        #endregion
    }

    public enum FactoryType { Service, Decorator, GenericWrapper };

    public enum FactoryCachePolicy { ShouldNotCacheExpression, CouldCacheExpression };

    public abstract class FactorySetup
    {
        public abstract FactoryType Type { get; }
        public virtual FactoryCachePolicy CachePolicy { get { return FactoryCachePolicy.ShouldNotCacheExpression; } }
        public virtual object Metadata { get { return null; } }
    }

    public class ServiceSetup : FactorySetup
    {
        public static readonly ServiceSetup Default = new ServiceSetup();

        public static ServiceSetup With(FactoryCachePolicy cachePolicy = FactoryCachePolicy.CouldCacheExpression, object metadata = null)
        {
            return cachePolicy == FactoryCachePolicy.CouldCacheExpression && metadata == null ? Default : new ServiceSetup(cachePolicy, metadata);
        }

        public static ServiceSetup WithMetadata(object metadata = null)
        {
            return metadata == null ? Default : new ServiceSetup(metadata: metadata);
        }

        public override FactoryType Type { get { return FactoryType.Service; } }
        public override FactoryCachePolicy CachePolicy { get { return _cachePolicy; } }
        public override object Metadata { get { return _metadata; } }

        #region Implementation

        private ServiceSetup(FactoryCachePolicy cachePolicy = FactoryCachePolicy.CouldCacheExpression, object metadata = null)
        {
            _cachePolicy = cachePolicy;
            _metadata = metadata;
        }

        private readonly FactoryCachePolicy _cachePolicy;

        private readonly object _metadata;

        #endregion
    }

    public class GenericWrapperSetup : FactorySetup
    {
        public static readonly GenericWrapperSetup Default = new GenericWrapperSetup();

        public static GenericWrapperSetup With(Func<Type[], Type> selectServiceTypeArg)
        {
            return selectServiceTypeArg == null ? Default : new GenericWrapperSetup(selectServiceTypeArg);
        }

        public override FactoryType Type { get { return FactoryType.GenericWrapper; } }
        public readonly Func<Type[], Type> GetWrappedServiceType;

        #region Implementation

        private GenericWrapperSetup(Func<Type[], Type> selectServiceTypeFromGenericArgs = null)
        {
            GetWrappedServiceType = selectServiceTypeFromGenericArgs ?? SelectSingleByDefault;
        }

        private static Type SelectSingleByDefault(Type[] typeArgs)
        {
            Throw.If(typeArgs.Length != 1, Error.GENERIC_WRAPPER_EXPECTS_SINGLE_TYPE_ARG_BY_DEFAULT, typeArgs);
            return typeArgs[0];
        }

        #endregion
    }

    public class DecoratorSetup : FactorySetup
    {
        public static readonly DecoratorSetup Default = new DecoratorSetup();

        public static DecoratorSetup With(Func<Request, bool> condition = null)
        {
            return condition == null ? Default : new DecoratorSetup(condition);
        }

        public override FactoryType Type { get { return FactoryType.Decorator; } }
        public readonly Func<Request, bool> IsApplicable;

        #region Implementation

        private DecoratorSetup(Func<Request, bool> condition = null)
        {
            IsApplicable = condition ?? (_ => true);
        }

        #endregion
    }

    public abstract class Factory
    {
        public static readonly FactorySetup DefaultSetup = ServiceSetup.Default;

        public FactoryWithContext WithContext(Request request, IRegistry registry)
        {
            return new FactoryWithContext(request, registry, this);
        }

        public readonly int ID;
        public readonly IReuse Reuse;

        public FactorySetup Setup
        {
            get { return _setup; }
            protected internal set { _setup = value ?? DefaultSetup; }
        }

        public virtual Type ImplementationType { get { return null; } }

        protected Factory(IReuse reuse = null, FactorySetup setup = null)
        {
            ID = Interlocked.Increment(ref _idSeedAndCount);
            Reuse = reuse;
            Setup = setup;
        }

        public virtual void ThrowIfCannotBeRegisteredWithServiceType(Type serviceType)
        {
            if (serviceType.IsGenericTypeDefinition && !ProvidesFactoryPerRequest)
                throw Error.UNABLE_TO_REGISTER_NON_FACTORY_PROVIDER_FOR_OPEN_GENERIC_SERVICE.Of(serviceType);
        }

        public virtual bool ProvidesFactoryPerRequest { get { return false; } }

        //ncrunch: no coverage start
        public virtual Factory GetFactoryPerRequestOrDefault(Request request, IRegistry registry) { return null; }
        //ncrunch: no coverage end

        public abstract Expression CreateExpression(Request request, IRegistry registry);

        public virtual LambdaExpression CreateFuncWithArgsOrDefault(Type funcType, Request request, IRegistry registry, out IList<Type> unusedFuncArgs)
        {
            unusedFuncArgs = null;
            return null;
        }

        public override string ToString()
        {
            return "Factory {ID=" + ID + (ImplementationType == null ? ""
                : ", ImplType=" + ImplementationType.Print()) + "}";
        }

        #region Implementation

        private static int _idSeedAndCount;
        private FactorySetup _setup;

        #endregion
    }

    public delegate ConstructorInfo GetConstructor(Type implementationType);

    public sealed class ReflectionFactory : Factory
    {
        public override Type ImplementationType { get { return _implementationType; } }

        public override bool ProvidesFactoryPerRequest { get { return _providesFactoryPerRequest; } }

        public ReflectionFactory(Type implementationType, IReuse reuse = null, GetConstructor getConstructor = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _implementationType = implementationType.ThrowIfNull()
                .ThrowIf(implementationType.IsAbstract, Error.EXPECTED_NON_ABSTRACT_IMPL_TYPE, implementationType);
            _providesFactoryPerRequest = _implementationType.IsGenericTypeDefinition;
            _getConstructor = getConstructor;
        }

        public override void ThrowIfCannotBeRegisteredWithServiceType(Type serviceType)
        {
            base.ThrowIfCannotBeRegisteredWithServiceType(serviceType);

            var implType = _implementationType;
            if (!implType.IsGenericTypeDefinition)
            {
                if (implType.IsGenericType && implType.ContainsGenericParameters)
                    throw Error.USUPPORTED_REGISTRATION_OF_NON_GENERIC_IMPL_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS.Of(
                        implType, implType.GetGenericTypeDefinition());

                if (implType != serviceType && serviceType != typeof(object) &&
                    Array.IndexOf(implType.GetImplementedTypes(), serviceType) == -1)
                    throw Error.EXPECTED_IMPL_TYPE_ASSIGNABLE_TO_SERVICE_TYPE.Of(implType, serviceType);
            }
            else if (implType != serviceType)
            {
                if (serviceType.IsGenericTypeDefinition)
                {
                    var implementedTypes = implType.GetImplementedTypes();
                    var implementedOpenGenericTypes = implementedTypes.Where(t =>
                        t.IsGenericType && t.ContainsGenericParameters && t.GetGenericTypeDefinition() == serviceType);

                    var implTypeArgs = implType.GetGenericArguments();
                    Throw.If(!implementedOpenGenericTypes.Any(t => t.ContainsAllGenericParameters(implTypeArgs)),
                        Error.UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_CAUSE_SERVICE_DOES_NOT_SPECIFY_ALL_TYPE_ARGS,
                        implType, serviceType, implementedOpenGenericTypes);
                }
                else if (implType.IsGenericType && serviceType.ContainsGenericParameters)
                    throw Error.USUPPORTED_REGISTRATION_OF_NON_GENERIC_SERVICE_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS.Of(
                        serviceType, serviceType.GetGenericTypeDefinition());
                else
                    throw Error.UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE.Of(implType, serviceType);
            }
        }

        public override Factory GetFactoryPerRequestOrDefault(Request request, IRegistry _)
        {
            var closedTypeArgs = _implementationType == request.OpenGenericServiceType
                ? request.ServiceType.GetGenericArguments()
                : GetClosedTypeArgsForGenericImplementationType(_implementationType, request);

            var closedImplType = _implementationType.MakeGenericType(closedTypeArgs);

            return new ReflectionFactory(closedImplType, Reuse, _getConstructor, Setup);
        }

        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            var ctor = GetConstructor(_implementationType);
            var ctorParams = ctor.GetParameters();
            Expression[] paramExprs = null;
            if (ctorParams.Length != 0)
            {
                paramExprs = new Expression[ctorParams.Length];
                for (var i = 0; i < ctorParams.Length; i++)
                {
                    var ctorParam = ctorParams[i];
                    var paramKey = request.FactoryType != FactoryType.Service ? request.ServiceKey // propagate key from wrapper or decorator.
                        : registry.ResolutionRules.ForConstructorParameter.Invoke(r => r(ctorParam, request, registry));
                    
                    var paramRequest = request.Push(ctorParam.ParameterType, paramKey,
                        new DependencyInfo(DependencyKind.CtorParam, ctorParam.Name));
                    
                    paramExprs[i] = registry.GetOrAddFactory(paramRequest, IfUnresolved.Throw).GetExpression();
                }
            }

            var newExpr = Expression.New(ctor, paramExprs);
            return InitMembersIfRequired(_implementationType, newExpr, request, registry);
        }

        public override LambdaExpression CreateFuncWithArgsOrDefault(Type funcType, Request request, IRegistry registry, out IList<Type> unusedFuncArgs)
        {
            var funcParamTypes = funcType.GetGenericArguments();
            funcParamTypes.ThrowIf(funcParamTypes.Length == 1, Error.EXPECTED_FUNC_WITH_MULTIPLE_ARGS, funcType);

            var ctor = GetConstructor(_implementationType);
            var ctorParams = ctor.GetParameters();
            var ctorParamExprs = new Expression[ctorParams.Length];
            var funcInputParamExprs = new ParameterExpression[funcParamTypes.Length - 1]; // (minus Func return parameter).

            for (var cp = 0; cp < ctorParams.Length; cp++)
            {
                var ctorParam = ctorParams[cp];
                for (var fp = 0; fp < funcParamTypes.Length - 1; fp++)
                {
                    var funcParamType = funcParamTypes[fp];
                    if (ctorParam.ParameterType == funcParamType &&
                        funcInputParamExprs[fp] == null) // Skip if Func parameter was already used for constructor.
                    {
                        ctorParamExprs[cp] = funcInputParamExprs[fp] = Expression.Parameter(funcParamType, ctorParam.Name);
                        break;
                    }
                }

                if (ctorParamExprs[cp] == null) // If no matching constructor parameter found in Func, resolve it from Container.
                {
                    var paramKey = request.FactoryType != FactoryType.Service ? request.ServiceKey // propagate key from wrapper or decorator.
                        : registry.ResolutionRules.ForConstructorParameter.Invoke(r => r(ctorParam, request, registry));
                    var paramRequest = request.Push(ctorParam.ParameterType, paramKey,
                        new DependencyInfo(DependencyKind.CtorParam, ctorParam.Name));
                    ctorParamExprs[cp] = registry.GetOrAddFactory(paramRequest, IfUnresolved.Throw).GetExpression();
                }
            }

            // Find unused Func parameters (present in Func but not in constructor) and create "_" (ignored) Parameter expressions for them.
            // In addition store unused parameter in output list for client review.
            unusedFuncArgs = null;
            for (var fp = 0; fp < funcInputParamExprs.Length; fp++)
            {
                if (funcInputParamExprs[fp] == null) // unused parameter
                {
                    if (unusedFuncArgs == null) unusedFuncArgs = new List<Type>(2);
                    var funcParamType = funcParamTypes[fp];
                    unusedFuncArgs.Add(funcParamType);
                    funcInputParamExprs[fp] = Expression.Parameter(funcParamType, "_");
                }
            }

            var newExpr = Expression.New(ctor, ctorParamExprs);
            var newExprInitialized = InitMembersIfRequired(_implementationType, newExpr, request, registry);
            return Expression.Lambda(funcType, newExprInitialized, funcInputParamExprs);
        }

        #region Implementation

        private readonly Type _implementationType;
        private readonly bool _providesFactoryPerRequest;
        private readonly GetConstructor _getConstructor;

        public ConstructorInfo GetConstructor(Type type)
        {
            if (_getConstructor != null)
                return _getConstructor(type);

            var constructors = type.GetConstructors();
            Throw.If(constructors.Length == 0, Error.NO_PUBLIC_CONSTRUCTOR_DEFINED, type);
            Throw.If(constructors.Length > 1, Error.UNABLE_TO_SELECT_CONSTRUCTOR, constructors.Length, type);
            return constructors[0];
        }

        private static Expression InitMembersIfRequired(Type implementationType, NewExpression newService, Request request, IRegistry registry)
        {
            if (registry.ResolutionRules.ForPropertyOrField.IsEmpty)
                return newService;

            var props = implementationType.GetProperties(Resolver.MembersToResolve).Where(p => p.GetSetMethod() != null);
            var fields = implementationType.GetFields(Resolver.MembersToResolve).Where(f => !f.IsInitOnly);

            var bindings = new List<MemberBinding>();
            foreach (var member in props.Cast<MemberInfo>().Concat(fields.Cast<MemberInfo>()))
            {
                var m = member;
                object key = null;
                if (registry.ResolutionRules.ForPropertyOrField.Invoke(r => r(out key, m, request, registry)))
                {
                    var type = m is PropertyInfo ? ((PropertyInfo)m).PropertyType : ((FieldInfo)m).FieldType;
                    var kind = m is PropertyInfo ? DependencyKind.Property : DependencyKind.Field;

                    var memberRequest = request.Push(type, key, new DependencyInfo(kind, m.Name));
                    var memberExpr = registry.GetOrAddFactory(memberRequest, IfUnresolved.Throw).GetExpression();
                    bindings.Add(Expression.Bind(m, memberExpr));
                }
            }

            return bindings.Count == 0 ? (Expression)newService : Expression.MemberInit(newService, bindings);
        }

        private static Type[] GetClosedTypeArgsForGenericImplementationType(Type implType, Request request)
        {
            var serviceTypeArgs = request.ServiceType.GetGenericArguments();
            var serviceTypeGenericDefinition = request.OpenGenericServiceType;

            var openImplTypeArgs = implType.GetGenericArguments();
            var implementedTypes = implType.GetImplementedTypes();

            Type[] resultImplTypeArgs = null;
            for (var i = 0; resultImplTypeArgs == null && i < implementedTypes.Length; i++)
            {
                var implementedType = implementedTypes[i];
                if (implementedType.IsGenericType && implementedType.ContainsGenericParameters &&
                    implementedType.GetGenericTypeDefinition() == serviceTypeGenericDefinition)
                {
                    var matchedTypeArgs = new Type[openImplTypeArgs.Length];
                    if (MatchServiceWithImplementedTypeArgs(ref matchedTypeArgs,
                        openImplTypeArgs, implementedType.GetGenericArguments(), serviceTypeArgs))
                        resultImplTypeArgs = matchedTypeArgs;
                }
            }

            resultImplTypeArgs = resultImplTypeArgs.ThrowIfNull(
                Error.UNABLE_TO_MATCH_IMPL_BASE_TYPES_WITH_SERVICE_TYPE, implType, implementedTypes, request);

            var unmatchedArgIndex = Array.IndexOf(resultImplTypeArgs, null);
            if (unmatchedArgIndex != -1)
                throw Error.UNABLE_TO_FIND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE.Of(
                    implType, openImplTypeArgs[unmatchedArgIndex], request);

            return resultImplTypeArgs;
        }

        private static bool MatchServiceWithImplementedTypeArgs(ref Type[] matchedImplArgs,
            Type[] openImplementationArgs, Type[] openImplementedArgs, Type[] closedServiceArgs)
        {
            for (var i = 0; i < openImplementedArgs.Length; i++)
            {
                var openImplementedArg = openImplementedArgs[i];
                var closedServiceArg = closedServiceArgs[i];
                if (openImplementedArg.IsGenericParameter)
                {
                    var matchedIndex = openImplementationArgs.IndexOf(t => t.Name == openImplementedArg.Name);
                    if (matchedIndex != -1)
                    {
                        if (matchedImplArgs[matchedIndex] == null)
                            matchedImplArgs[matchedIndex] = closedServiceArg;
                        else if (matchedImplArgs[matchedIndex] != closedServiceArg)
                            return false; // more than one closedServiceArg is matching with single openArg
                    }
                }
                else if (openImplementedArg != closedServiceArg)
                {
                    if (!openImplementedArg.IsGenericType || !openImplementedArg.ContainsGenericParameters ||
                        !closedServiceArg.IsGenericType ||
                        closedServiceArg.GetGenericTypeDefinition() != openImplementedArg.GetGenericTypeDefinition())
                        return false; // openArg and closedArg are different types

                    if (!MatchServiceWithImplementedTypeArgs(ref matchedImplArgs, openImplementationArgs,
                        openImplementedArg.GetGenericArguments(), closedServiceArg.GetGenericArguments()))
                        return false; // nested match failed due either one of above reasons.
                }
            }

            return true;
        }

        #endregion
    }

    public sealed class DelegateFactory : Factory
    {
        public DelegateFactory(Func<Request, IRegistry, Expression> getExpression, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _getExpression = getExpression.ThrowIfNull();
        }

        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            return _getExpression(request, registry).ThrowIfNull(Error.DELEGATE_FACTORY_EXPRESSION_RETURNED_NULL, request);
        }

        #region Implementation

        private readonly Func<Request, IRegistry, Expression> _getExpression;

        #endregion
    }

    public sealed class FactoryProvider : Factory
    {
        public override bool ProvidesFactoryPerRequest { get { return true; } }

        public FactoryProvider(Func<Request, IRegistry, Factory> getFactoryOrDefault, FactorySetup setup = null)
            : base(setup: setup)
        {
            _getFactoryOrDefault = getFactoryOrDefault.ThrowIfNull();
        }

        public override Factory GetFactoryPerRequestOrDefault(Request request, IRegistry registry)
        {
            var factory = _getFactoryOrDefault(request, registry);
            if (factory != null && factory.Setup == DefaultSetup)
                factory.Setup = Setup; // propagate provider setup if it is not specified by client.
            return factory;
        }

        //ncrunch: no coverage start
        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            throw new NotSupportedException();
        }
        //ncrunch: no coverage end

        private readonly Func<Request, IRegistry, Factory> _getFactoryOrDefault;
    }

    public enum DependencyKind { CtorParam, Property, Field }

    public sealed class DependencyInfo
    {
        public readonly DependencyKind Kind;
        public readonly string Name;

        public DependencyInfo(DependencyKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public override string ToString()
        {
            return Kind + " " + Name;
        }
    }

    public sealed class Scope : IDisposable
    {
        public static readonly MethodInfo GetOrAddMethod = typeof(Scope).GetMethod("GetOrAdd");
        public T GetOrAdd<T>(int id, Func<T> factory)
        {
            Throw.If(_disposed == 1, Error.SCOPE_IS_DISPOSED);
            lock (_syncRoot)
            {
                var item = _items.GetFirstValueOfHashOrDefault(id);
                if (item == null)
                    _items = _items.AddOrUpdate(id, item = factory());
                return (T)item;
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            lock (_syncRoot)
            {
                if (!_items.IsEmpty)
                    foreach (var item in _items.Enumerate().Select(x => x.Value).OfType<IDisposable>())
                        item.Dispose();
                _items = null;
            }
        }

        #region Implementation

        private HashTree<int, object> _items = HashTree<int, object>.Empty;
        private int _disposed;

        // Sync root is required to create single only instance of item. The same as for Lazy<T>
        private readonly object _syncRoot = new object();

        #endregion
    }

    public interface IReuse
    {
        Expression Apply(Request request, IRegistry registry, int factoryID, Expression factoryExpr);
    }

    public static class Reuse
    {
        public static readonly IReuse Transient = null; // no reuse.
        public static readonly IReuse Singleton, InScope, InResolution;

        static Reuse()
        {
            Singleton = new SingletonReuse();
            InScope = new ScopedReuse(ResolutionRoot.ReusedInScopeParameter);
            InResolution = new ScopedReuse(Expression.Call(GetScopeMethod, ResolutionRoot.ReusedInResolutionParameter));
        }

        public static Expression GetScopedServiceExpression(Expression scope, int factoryID, Expression factoryExpr)
        {
            return Expression.Call(scope,
                Scope.GetOrAddMethod.MakeGenericMethod(factoryExpr.Type),
                Expression.Constant(factoryID), Expression.Lambda(factoryExpr, null));
        }

        public static readonly MethodInfo GetScopeMethod = typeof(Reuse).GetMethod("GetScope");
        public static Scope GetScope(ref Scope scope)
        {
            return scope = scope ?? new Scope();
        }

        public sealed class ScopedReuse : IReuse
        {
            public ScopedReuse(Expression scopeExpr)
            {
                _scopeExpr = scopeExpr;
            }

            public Expression Apply(Request _, IRegistry __, int factoryID, Expression factoryExpr)
            {
                return GetScopedServiceExpression(_scopeExpr, factoryID, factoryExpr);
            }

            private readonly Expression _scopeExpr;
        }

        public sealed class SingletonReuse : IReuse
        {
            public Expression Apply(Request request, IRegistry registry, int factoryID, Expression factoryExpr)
            {
                // Create lazy singleton if we have Func somewhere in dependency chain.
                var parent = request.Parent;
                if (parent != null && parent.Enumerate().Any(p =>
                {
                    var openGenericServiceType = p.OpenGenericServiceType;
                    return openGenericServiceType != null && OpenGenericsSupport.FuncTypes.Contains(openGenericServiceType);
                }))
                    return GetScopedServiceExpression(
                        request.Root.GetItemExpression(registry.Singletons),
                        factoryID, factoryExpr);

                // Create singleton object now and put it into store.
                var currentScope = registry.ReusedInScope;
                var singleton = registry.Singletons.GetOrAdd(factoryID,
                    () => factoryExpr.CompileToFactory().Invoke(request.Root.Store.Value, currentScope, null));
                return request.Root.GetItemExpression(singleton, factoryExpr.Type);
            }
        }
    }

    public enum IfUnresolved { Throw, ReturnNull }

    public interface IResolver
    {
        object ResolveDefault(Type serviceType, IfUnresolved ifUnresolved);

        object ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved);
    }

    public enum IfAlreadyRegistered { ThrowIfNamed, KeepAlreadyRegistered }

    public interface IRegistrator
    {
        void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered);

        bool IsRegistered(Type serviceType, string serviceName);
    }

    public interface IRegistry : IResolver, IRegistrator
    {
        RegistryWeakRef SelfWeakRef { get; }
        ResolutionRules ResolutionRules { get; }
        Scope ReusedInScope { get; }
        Scope Singletons { get; }

        FactoryWithContext GetOrAddFactory(Request request, IfUnresolved ifUnresolved);

        Factory GetFactoryOrDefault(Type serviceType, object serviceKey);
        Factory GetGenericWrapperOrDefault(Type openGenericServiceType);
        Expression GetDecoratorExpressionOrDefault(Request request);

        IEnumerable<KV<object, Factory>> GetAllFactories(Type serviceType);

        Type GetWrappedServiceTypeOrSelf(Type serviceType);
    }

    public class FactoryWithContext
    {
        public readonly Factory Factory;
        public readonly Request Request;
        public readonly IRegistry Registry;

        public FactoryWithContext(Request request, IRegistry registry, Factory factory)
        {
            Factory = factory;
            Request = request.ResolveWith(factory);
            Registry = registry;
        }

        public Expression GetExpression()
        {
            var decorator = Registry.GetDecoratorExpressionOrDefault(Request);
            if (decorator != null && !(decorator is LambdaExpression))
                return decorator;

            var expression = Request.Root.GetCachedFactoryExpression(Factory.ID);
            if (expression == null)
            {
                expression = Factory.CreateExpression(Request, Registry);
                if (Factory.Reuse != null)
                    expression = Factory.Reuse.Apply(Request, Registry, Factory.ID, expression);
                if (Factory.Setup.CachePolicy == FactoryCachePolicy.CouldCacheExpression)
                    Request.Root.CacheFactoryExpression(Factory.ID, expression);
            }

            if (decorator != null)
                expression = Expression.Invoke(decorator, expression);

            return expression;
        }

        public LambdaExpression GetFuncWithArgsOrDefault(Type funcType, out IList<Type> unusedFuncArgs)
        {
            var func = Factory.CreateFuncWithArgsOrDefault(funcType, Request, Registry, out unusedFuncArgs);
            if (func == null)
                return null;

            var decorator = Registry.GetDecoratorExpressionOrDefault(Request);
            if (decorator != null && !(decorator is LambdaExpression))
                return Expression.Lambda(funcType, decorator, func.Parameters);

            if (Factory.Reuse != null)
                func = Expression.Lambda(funcType, Factory.Reuse.Apply(Request, Registry, Factory.ID, func.Body), func.Parameters);

            if (decorator != null)
                func = Expression.Lambda(funcType, Expression.Invoke(decorator, func.Body), func.Parameters);

            return func;
        }
    }

    public sealed class Many<TService>
    {
        public readonly IEnumerable<TService> Items;

        public Many(IEnumerable<TService> items)
        {
            Items = items;
        }
    }

    public sealed class Meta<TService, TMetadata>
    {
        public readonly TService Value;

        public readonly TMetadata Metadata;

        public Meta(TService service, TMetadata metadata)
        {
            Value = service;
            Metadata = metadata;
        }
    }

    [DebuggerDisplay("{Expression}")]
    public sealed class DebugExpression<TService>
    {
        public readonly Expression<CompiledFactory> Expression;

        public DebugExpression(Expression<CompiledFactory> expression)
        {
            Expression = expression;
        }
    }

    public class ContainerException : InvalidOperationException
    {
        public ContainerException(string message) : base(message) { }
    }

    public static class Throw
    {
        public static Func<string, Exception> GetException = message => new ContainerException(message);

        public static Func<object, string> PrintArg = PrintTools.Print;

        public static T ThrowIfNull<T>(this T arg, string message = null, object arg0 = null, object arg1 = null, object arg2 = null) where T : class
        {
            if (arg != null) return arg;
            throw GetException(message == null ? Format(ERROR_ARG_IS_NULL, typeof(T)) : Format(message, arg0, arg1, arg2));
        }

        public static T ThrowIf<T>(this T arg, bool throwCondition, string message = null, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            if (!throwCondition) return arg;
            throw GetException(message == null ? Format(ERROR_ARG_HAS_IMVALID_CONDITION, typeof(T)) : Format(message, arg0, arg1, arg2));
        }

        public static void If(bool throwCondition, string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            if (!throwCondition) return;
            throw GetException(Format(message, arg0, arg1, arg2));
        }

        public static Exception Of(this string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            return GetException(Format(message, arg0, arg1, arg2));
        }

        private static string Format(this string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            return string.Format(message, PrintArg(arg0), PrintArg(arg1), PrintArg(arg2));
        }

        public static readonly string ERROR_ARG_IS_NULL = "Argument of type {0} is null.";
        public static readonly string ERROR_ARG_HAS_IMVALID_CONDITION = "Argument of type {0} has invalid condition.";
    }

    public static class TypeTools
    {
        // ReSharper disable ConstantNullCoalescingCondition
        public static Func<Type, string> PrintDetailsDefault = t => t.FullName ?? t.Name;
        // ReSharper restore ConstantNullCoalescingCondition

        public static string Print(this Type type, Func<Type, string> printDetails = null)
        {
            if (type == null) return null;
            printDetails = printDetails ?? PrintDetailsDefault;
            var name = printDetails(type);
            if (type.IsGenericType) // for generic types
            {
                var genericArgs = type.GetGenericArguments();
                var genericArgsString = type.IsGenericTypeDefinition
                    ? new string(',', genericArgs.Length - 1)
                    : String.Join(", ", genericArgs.Select(x => x.Print(printDetails)).ToArray());
                name = name.Substring(0, name.IndexOf('`')) + "<" + genericArgsString + ">";
            }
            return name.Replace('+', '.'); // for nested classes
        }

        public enum IncludeItself { No, AsFirst }

        /// <summary>
        /// Returns all type interfaces and base types except object.
        /// </summary>
        public static Type[] GetImplementedTypes(this Type type, IncludeItself includeItself = IncludeItself.No)
        {
            Type[] results;

            var interfaces = type.GetInterfaces();
            var interfaceStartIndex = includeItself == IncludeItself.AsFirst ? 1 : 0;
            var selfPlusInterfaceCount = interfaceStartIndex + interfaces.Length;

            var baseType = type.BaseType;
            if (baseType == null || baseType == typeof(object))
                results = new Type[selfPlusInterfaceCount];
            else
            {
                List<Type> baseBaseTypes = null;
                for (var bb = baseType.BaseType; bb != null && bb != typeof(object); bb = bb.BaseType)
                    (baseBaseTypes ?? (baseBaseTypes = new List<Type>(2))).Add(bb);

                if (baseBaseTypes == null)
                    results = new Type[selfPlusInterfaceCount + 1];
                else
                {
                    results = new Type[selfPlusInterfaceCount + 1 + baseBaseTypes.Count];
                    baseBaseTypes.CopyTo(results, selfPlusInterfaceCount + 1);
                }

                results[selfPlusInterfaceCount] = baseType;
            }

            if (includeItself == IncludeItself.AsFirst)
                results[0] = type;

            if (interfaces.Length == 1)
                results[interfaceStartIndex] = interfaces[0];
            else if (interfaces.Length > 1)
                Array.Copy(interfaces, 0, results, interfaceStartIndex, interfaces.Length);

            return results;
        }

        public static bool ContainsAllGenericParameters(this Type similarType, Type[] genericParameters)
        {
            var argNames = new string[genericParameters.Length];
            for (var i = 0; i < genericParameters.Length; i++)
                argNames[i] = genericParameters[i].Name;

            MarkTargetGenericParameters(similarType.GetGenericArguments(), ref argNames);

            for (var i = 0; i < argNames.Length; i++)
                if (argNames[i] != null)
                    return false;

            return true;
        }

        #region Implementation

        private static void MarkTargetGenericParameters(Type[] sourceTypeArgs, ref string[] targetArgNames)
        {
            for (var i = 0; i < sourceTypeArgs.Length; i++)
            {
                var sourceTypeArg = sourceTypeArgs[i];
                if (sourceTypeArg.IsGenericParameter)
                {
                    var matchingTargetArgIndex = Array.IndexOf(targetArgNames, sourceTypeArg.Name);
                    if (matchingTargetArgIndex != -1)
                        targetArgNames[matchingTargetArgIndex] = null;
                }
                else if (sourceTypeArg.IsGenericType && sourceTypeArg.ContainsGenericParameters)
                    MarkTargetGenericParameters(sourceTypeArg.GetGenericArguments(), ref targetArgNames);
            }
        }

        #endregion
    }

    public static class ArrayTools
    {
        public static T[] Append<T>(this T[] source, params T[] added)
        {
            if (added == null || added.Length == 0)
                return source;
            if (source == null || source.Length == 0)
                return added;
            var result = new T[source.Length + added.Length];
            Array.Copy(source, 0, result, 0, source.Length);
            if (added.Length == 1)
                result[source.Length] = added[0];
            else
                Array.Copy(added, 0, result, source.Length, added.Length);
            return result;
        }

        public static T[] AppendOrUpdate<T>(this T[] source, T value, int index = -1)
        {
            if (source == null || source.Length == 0)
                return new[] { value };
            var sourceLength = source.Length;
            index = index < 0 ? sourceLength : index;
            var result = new T[index < sourceLength ? sourceLength : sourceLength + 1];
            Array.Copy(source, result, sourceLength);
            result[index] = value;
            return result;
        }

        public static int IndexOf<T>(this T[] source, Func<T, bool> predicate)
        {
            if (source == null || source.Length == 0)
                return -1;
            for (var i = 0; i < source.Length; ++i)
                if (predicate(source[i]))
                    return i;
            return -1;
        }

        public static T[] Remove<T>(this T[] source, T value)
        {
            if (source == null || source.Length == 0)
                return source;
            var valueIndex = source.IndexOf(x => Equals(x, value));
            if (valueIndex == -1)
                return source;
            if (source.Length == 1)
                return new T[0];
            var result = new T[source.Length - 1];
            if (valueIndex != 0)
                Array.Copy(source, 0, result, 0, valueIndex);
            if (valueIndex != result.Length)
                Array.Copy(source, valueIndex + 1, result, valueIndex, result.Length - valueIndex);
            return result;
        }
    }

    public static class PrintTools
    {
        public static string Print(object x)
        {
            return x is string ? (string)x
                : (x is Type ? ((Type)x).Print()
                : (x is IEnumerable<Type> ? ((IEnumerable)x).Print(";" + Environment.NewLine, ifEmpty: "<empty>")
                : (x is IEnumerable ? ((IEnumerable)x).Print(ifEmpty: "<empty>")
                : (string.Empty + x))));
        }

        public static string Print(this IEnumerable items,
            string separator = ", ", Func<object, string> printItem = null, string ifEmpty = null)
        {
            if (items == null) return null;
            printItem = printItem ?? Print;
            var builder = new StringBuilder();
            foreach (var item in items)
                (builder.Length == 0 ? builder : builder.Append(separator)).Append(printItem(item));
            var result = builder.ToString();
            return result != string.Empty ? result : (ifEmpty ?? string.Empty);
        }
    }

    public class KV<K, V>
    {
        public readonly K Key;
        public readonly V Value;

        public KV(K key, V value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// Immutable kind of http://en.wikipedia.org/wiki/AVL_tree where actual node key is hash code of <typeparamref name="K"/>.
    /// </summary>
    public sealed class HashTree<K, V>
    {
        public static readonly HashTree<K, V> Empty = new HashTree<K, V>();

        public readonly K Key;
        public readonly V Value;

        public readonly int Hash;
        public readonly KV<K, V>[] Conflicts;
        public readonly HashTree<K, V> Left, Right;
        public readonly int Height;

        public bool IsEmpty { get { return Height == 0; } }

        public delegate V UpdateValue(V oldValue, V value);

        public HashTree<K, V> AddOrUpdate(K key, V value, UpdateValue updateValue = null)
        {
            return AddOrUpdate(key.GetHashCode(), key, value, updateValue);
        }

        public V GetValueOrDefault(K key, V defaultValue = default(V))
        {
            var t = this;
            var hash = key.GetHashCode();
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 && (ReferenceEquals(key, t.Key) || key.Equals(t.Key)) ? t.Value
                : t.GetConflictedValueOrDefault(key, defaultValue);
        }

        public V GetFirstValueOfHashOrDefault(int hash, V defaultValue = default(V))
        {
            var t = this;
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 ? t.Value : defaultValue;
        }

        /// <summary>
        /// Depth-first in-order traversal as described in http://en.wikipedia.org/wiki/Tree_traversal
        /// The only difference is using fixed size array instead of stack for speed-up (~20% faster than stack).
        /// </summary>
        public IEnumerable<KV<K, V>> Enumerate()
        {
            var parents = new HashTree<K, V>[Height];
            var parentCount = -1;
            var t = this;
            while (!t.IsEmpty || parentCount != -1)
            {
                if (!t.IsEmpty)
                {
                    parents[++parentCount] = t;
                    t = t.Left;
                }
                else
                {
                    t = parents[parentCount--];
                    yield return new KV<K, V>(t.Key, t.Value);
                    if (t.Conflicts != null)
                        for (var i = 0; i < t.Conflicts.Length; i++)
                            yield return t.Conflicts[i];
                    t = t.Right;
                }
            }
        }

        #region Implementation

        private HashTree() { }

        private HashTree(int hash, K key, V value, KV<K, V>[] conficts, HashTree<K, V> left, HashTree<K, V> right)
        {
            Hash = hash;
            Key = key;
            Value = value;
            Conflicts = conficts;
            Left = left;
            Right = right;
            Height = 1 + (left.Height > right.Height ? left.Height : right.Height);
        }

        private HashTree<K, V> AddOrUpdate(int hash, K key, V value, UpdateValue updateValue)
        {
            return Height == 0 ? new HashTree<K, V>(hash, key, value, null, Empty, Empty)
                : (hash == Hash ? UpdateValueAndResolveConflicts(key, value, updateValue)
                : (hash < Hash
                    ? With(Left.AddOrUpdate(hash, key, value, updateValue), Right)
                    : With(Left, Right.AddOrUpdate(hash, key, value, updateValue)))
                        .EnsureBalanced());
        }

        private HashTree<K, V> UpdateValueAndResolveConflicts(K key, V value, UpdateValue updateValue)
        {
            if (ReferenceEquals(Key, key) || Key.Equals(key))
                return new HashTree<K, V>(Hash, key, updateValue == null ? value : updateValue(Value, value), Conflicts, Left, Right);

            if (Conflicts == null)
                return new HashTree<K, V>(Hash, Key, Value, new[] { new KV<K, V>(key, value) }, Left, Right);

            var i = Conflicts.Length - 1;
            while (i >= 0 && !Equals(Conflicts[i].Key, Key)) i--;
            var conflicts = new KV<K, V>[i != -1 ? Conflicts.Length : Conflicts.Length + 1];
            Array.Copy(Conflicts, 0, conflicts, 0, Conflicts.Length);
            conflicts[i != -1 ? i : Conflicts.Length] =
                new KV<K, V>(key, i != -1 && updateValue != null ? updateValue(Conflicts[i].Value, value) : value);
            return new HashTree<K, V>(Hash, Key, Value, conflicts, Left, Right);
        }

        private V GetConflictedValueOrDefault(K key, V defaultValue)
        {
            if (Conflicts != null)
                for (var i = 0; i < Conflicts.Length; i++)
                    if (Equals(Conflicts[i].Key, key))
                        return Conflicts[i].Value;
            return defaultValue;
        }

        private HashTree<K, V> EnsureBalanced()
        {
            var delta = Left.Height - Right.Height;
            return delta >= 2 ? With(Left.Right.Height - Left.Left.Height == 1 ? Left.RotateLeft() : Left, Right).RotateRight()
                : (delta <= -2 ? With(Left, Right.Left.Height - Right.Right.Height == 1 ? Right.RotateRight() : Right).RotateLeft()
                : this);
        }

        private HashTree<K, V> RotateRight()
        {
            return Left.With(Left.Left, With(Left.Right, Right));
        }

        private HashTree<K, V> RotateLeft()
        {
            return Right.With(With(Left, Right.Left), Right.Right);
        }

        private HashTree<K, V> With(HashTree<K, V> left, HashTree<K, V> right)
        {
            return new HashTree<K, V>(Hash, Key, Value, Conflicts, left, right);
        }

        #endregion
    }

    public static class Ref
    {
        public static Ref<T> Of<T>(T value)
        {
            return new Ref<T>(value);
        }
    }

    public sealed class Ref<T>
    {
        public T Value { get { return _value; } }

        public Ref(T initialValue = default(T))
        {
            _value = initialValue;
        }

        public T Update(Func<T, T> update)
        {
            var retryCount = 0;
            while (true)
            {
                var version = _version; // remember current version before update
                var oldValue = _value;
                var newValue = update(oldValue);

                // If no other commits in progress, mark that our commit is in progress and Try to commit updated value.
                // If succeeded return old value used for update. Otherwise retry.
                if (Interlocked.CompareExchange(ref _isCommitInProgress, 1, 0) == 0)
                {
                    try
                    {
                        // If some other code did not change version yet (means old value is consistent with updated one).
                        // Then save updated value and increment version to mark the change for other committers.
                        if (version == _version)
                        {
                            _value = newValue;
                            Interlocked.Increment(ref _version);
                            return oldValue; // return snapshot used for update
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isCommitInProgress, 0);
                    }
                }
                else if (retryCount++ < RETRY_UNTIL_THROW_COUNT)
                {
                    var awaitsCount = 0; // Before retry wait a bit for finished commit of other committer.
                    while (_isCommitInProgress == 1 && awaitsCount++ < AWAIT_LOOPS_PER_RETRY_COUNT)
                        Thread.Sleep(1); // Not a Sleep(0) because of http://joeduffyblog.com/2006/08/22/priorityinduced-starvation-why-sleep1-is-better-than-sleep0-and-the-windows-balance-set-manager/
                }
                else throw new InvalidOperationException(ERROR_EXCEEDED_RETRY_COUNT);
            }
        }

        #region Implementation

        private const int RETRY_UNTIL_THROW_COUNT = 10;
        private const int AWAIT_LOOPS_PER_RETRY_COUNT = 100;

        private static readonly string ERROR_EXCEEDED_RETRY_COUNT =
            "Ref tried to commit update for " + RETRY_UNTIL_THROW_COUNT + " times But there is always someone else intervened.";

        private T _value;
        private int _version;
        private int _isCommitInProgress; // 0 means false

        #endregion
    }
}