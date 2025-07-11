# Http Cache Headers Middleware for ASP.NET Core
ASP.NET Core middleware that adds HttpCache headers to responses (Cache-Control, Expires, ETag, Last-Modified), and implements cache expiration &amp; validation models.  It can be used to ensure caches correctly cache responses and/or to implement concurrency for REST-based APIs using ETags.

The middleware itself **does not store responses**. What it does is generate the correct cache-related headers, and ensure a cache can check for expiration (304 Not Modified) & preconditions (412 Precondition Failed) (often used for concurrency checks).  For more information on caches, the different models and the related headers, have a look at https://developer.mozilla.org/en-US/docs/Web/HTTP/Guides/Caching.

This middleware can be used together with a shared cache, a private cache or both.  For production scenarios the best approach is to use this middleware to generate the ETags, combined with a cache server or CDN to inspect those tags and effectively cache the responses.  In the sample, the Microsoft.AspNetCore.ResponseCaching cache store is used to cache the responses.  

[![NuGet version](https://badge.fury.io/nu/marvin.cache.headers.svg)](https://badge.fury.io/nu/marvin.cache.headers)
 
# Installation (NuGet)
````powershell
Install-Package Marvin.Cache.Headers
````

# Usage 

First, register the services with ASP.NET Core's dependency injection container (in the ConfigureServices method on the Startup class)

````csharp
services.AddHttpCacheHeaders();
````

Then, add the middleware to the request pipeline.  Starting with version 6.0, the middleware MUST be added between UseRouting() and UseEndpoints().  

````csharp
app.UseRouting(); 

app.UseHttpCacheHeaders();

app.UseEndpoints(...);
````

# Configuring Options

The middleware allows customization of how headers are generated. The AddHttpCacheHeaders() method has parameters for configuring options related to expiration, validation and middleware.  

For example, this code will set the max-age directive to 600 seconds, add the must-revalidate directive and ignore header generation for all responses with status code 500.

````csharp
services.AddHttpCacheHeaders(
    expirationModelOptions =>
    {
        expirationModelOptions.MaxAge = 600;
    },
    validationModelOptions =>
    {
        validationModelOptions.MustRevalidate = true;
    },
    middlewareOptions => 
    {
        middlewareOptions.IgnoreStatusCodes = new[] { 500 };
    });
````

There are some predefined collections with status codes you can use when you want to ignore:
- all server errors `HttpStatusCodes.ServerErrors`
- all client errors `HttpStatusCodes.ClientErrors`
- all errors `HttpStatusCodes.AllErrors`

# Action (Resource) and Controller-level Header Configuration

For anything but the simplest of cases having one global cache policy isn't sufficient: configuration at level of each resource (action/controller) is required.  For those cases, use the HttpCacheExpiration and/or HttpCacheValidation attributes at action or controller level.  

````csharp
[HttpGet]
[HttpCacheExpiration(CacheLocation = CacheLocation.Public, MaxAge = 99999)]
[HttpCacheValidation(MustRevalidate = true)]
public IEnumerable<string> Get()
{
    return new[] { "value1", "value2" };
}
```
Both override the global options.  Action-level configuration overrides controller-level configuration.

# Ignoring Cache Headers / eTag Generation

You don't always want tags / headers to be generated for all resources (e.g.: for a large file).  You can ignore generation by applying the HttpCacheIgnore attribute at controller or action level. 

````csharp
[HttpGet]
[HttpCacheIgnore]
public IEnumerable<string> Get()
{
    return new[] { "value1", "value2" };
}
````

If you want to globally disable automatic header generation, you can do so by setting DisableGlobalHeaderGeneration on the middleware options to true.

````csharp
services.AddHttpCacheHeaders(     
    middlewareOptionsAction: middlewareOptions => 
    {
        middlewareOptions.DisableGlobalHeaderGeneration = true;
    });
````

# Marking for Invalidation
Cache invalidation essentially means wiping a response from the cache because you know it isn't the correct version anymore. Caches often partially automate this (a response can be invalidated when it becomes stale, for example) and/or expose an API to manually invalidate items.  

The same goes for the cache headers middleware, which holds a store of records with previously generated cache headers & tags.  Replacement of store key records (/invalidation) is mostly automatic.  Say you're interacting with values/1. First time the backend is hit and you get back an eTag in the response headers. Next request you send is again a GET request with the "If-None-Match"-header set to the eTag: the backend won't be hit. Then, you send a PUT request to values/1, which potentially results in a change; if you send a GET request now, the backend will be hit again.  

However: if you're updating/changing resources by using an out of band mechanism (eg: a backend process that changes the data in your database, or a resource gets updated that has an update of related resources as a side effect), this process can't be automated.  

Take a list of employees as an example.  If a PUT statement is sent to one "employees" resource, then that one "employees" resource will get a new Etag.  Yet: if you're sending a PUT request to one specific employee ("employees/1", "employees/2", ...), this might have the effect that the "employees" resource has also changed: if the employee you just updated is one of the employees in the returned employees list when fetching the "employees" resource, the "employees" resource is out of date.  Same goes for deleting or creating an employee: that, too, might have an effect on the "employees" resource.  

To support this scenario the cache headers middleware allows marking an item for invalidation.  When doing that, the related item will be removed from the internal store, meaning that for subsequent requests a stored item will not be found.  

To use this, inject an IValidatorValueInvalidator and call MarkForInvalidation on it, passing through the key(s) of the item(s) you want to be removed.  You can additionally inject an IStoreKeyAccessor, which contains methods that make it easy to find one or more keys from (part of) a URI.  

# Extensibility

The middleware is very extensible. If you have a look at the AddHttpCacheHeaders method you'll notice it allows injecting custom implementations of 
IValidatorValueStore, IStoreKeyGenerator, IETagGenerator and/or IDateParser (via actions). 

## IValidatorValueStore

A validator value store stores validator values.  A validator value is used by the cache validation model when checking if a cached item is still valid.  It contains ETag and LastModified properties.  The default IValidatorValueStore implementation (InMemoryValidatorValueStore) is an in-memory store that stores items in a ConcurrentDictionary<string, ValidatorValue>. 

````csharp
/// <summary>
/// Contract for a store for validator values.  Each item is stored with a <see cref="StoreKey" /> as key```
/// and a <see cref="ValidatorValue" /> as value (consisting of an ETag and Last-Modified date).   
/// </summary>
public interface IValidatorValueStore
{
    /// <summary>
    /// Get a value from the store.
    /// </summary>
    /// <param name="key">The <see cref="StoreKey"/> of the value to get.</param>
    /// <returns></returns>
    Task<ValidatorValue> GetAsync(StoreKey key);
    /// <summary>
    /// Set a value in the store.
    /// </summary>
    /// <param name="key">The <see cref="StoreKey"/> of the value to store.</param>
    /// <param name="validatorValue">The <see cref="ValidatorValue"/> to store.</param>
    /// <returns></returns>
    Task SetAsync(StoreKey key, ValidatorValue validatorValue);

    /// <summary>
    /// Find one or more keys that contain the inputted valueToMatch 
    /// </summary>
    /// <param name="valueToMatch">The value to match as part of the key</param>
    /// <param name="ignoreCase">Ignore case when matching</param>
    /// <returns></returns>
    Task<IEnumerable<StoreKey>> FindStoreKeysByKeyPartAsync(string valueToMatch, bool ignoreCase);
}
````

BREAKING CHANGE from v7 onwards: the FindStoreKeysByKeyPartAsync methods return an IAsyncEnumerable<StoreKey> to enable async streaming of results.

````csharp
/// <summary>
/// Contract for a store for validator values.  Each item is stored with a <see cref="StoreKey" /> as key```
/// and a <see cref="ValidatorValue" /> as value (consisting of an ETag and Last-Modified date).   
/// </summary>
public interface IValidatorValueStore
{
    /// <summary>
    /// Get a value from the store.
    /// </summary>
    /// <param name="key">The <see cref="StoreKey"/> of the value to get.</param>
    /// <returns></returns>
    Task<ValidatorValue> GetAsync(StoreKey key);
    /// <summary>
    /// Set a value in the store.
    /// </summary>
    /// <param name="key">The <see cref="StoreKey"/> of the value to store.</param>
    /// <param name="validatorValue">The <see cref="ValidatorValue"/> to store.</param>
    /// <returns></returns>
    Task SetAsync(StoreKey key, ValidatorValue validatorValue);

    /// <summary>
    /// Find one or more keys that contain the inputted valueToMatch 
    /// </summary>
    /// <param name="valueToMatch">The value to match as part of the key</param>
    /// <param name="ignoreCase">Ignore case when matching</param>
    /// <returns></returns>
    IAsyncEnumerable<StoreKey> FindStoreKeysByKeyPartAsync(string valueToMatch, bool ignoreCase);
}
````

## IStoreKeyGenerator
The StoreKey, as used by the IValidatorValueStore as key, can be customized as well.  To do so, implement the IStoreKeyGenerator interface.  The default implementation (DefaultStoreKeyGenerator) generates a key from the request path, request query string and request header values (taking VaryBy into account). Through StoreKeyContext you can access all applicable values that can be useful for generating such a key. 

````csharp
/// <summary>
/// Contract for a key generator, used to generate a <see cref="StoreKey" /> ```
/// </summary>
public interface IStoreKeyGenerator
{
    /// <summary>
    /// Generate a key for storing a <see cref="ValidatorValue"/> in a <see cref="IValidatorValueStore"/>.
    /// </summary>
    /// <param name="context">The <see cref="StoreKeyContext"/>.</param>         
    /// <returns></returns>
    Task<StoreKey> GenerateStoreKey(
        StoreKeyContext context);
}
````

## IETagGenerator

You can inject an IETagGenerator-implementing class to modify how ETags are generated (ETags are part of a ValidatorValue). The default implementation (DefaultStrongETagGenerator) generates strong Etags from the request key + response body (MD5 hash from combined bytes). 

````csharp
/// <summary>
/// Contract for an E-Tag Generator, used to generate the unique weak or strong E-Tags for cache items
/// </summary>
public interface IETagGenerator
{
    Task<ETag> GenerateETag(
        StoreKey storeKey,
        string responseBodyContent);
}
````
## IETagInjector

You can inject an IETagInjector-implementing class to modify how, where and when ETags are provided. The default implementation (DefaultETagInjector) injects the DefaultETag generator using the response body on the http context as the string along with the provided request key.

````csharp
/// <summary>
///     Contract for a ETagInjector, which can be used to inject custom eTags for resources
///     of which may be injected in the request pipeline (eg: based on existing calculated eTag on resource and store key)
/// </summary>
/// <remarks>
///     This injector will wrap the <see cref="IETagGenerator" /> to allow for eTag source to be swapped out
///     based on the <see cref="HttpContext" /> (rather than extend the interface of <see cref="IETagInjector" /> to
///     to extended including the <see cref="HttpContext" />
/// </remarks>
public interface IETagInjector
{
    Task<ETag> RetrieveETag(ETagContext eTagContext);
}
````

## ILastModifiedInjector

You can inject an ILastModifiedInjector-implementing class to modify how LastModified values are provided. The default implementation (DefaultLastModifiedInjector) injects the current UTC. 

````csharp
/// <summary>
/// Contract for a LastModifiedInjector, which can be used to inject custom last modified dates for resources
/// of which you know when they were last modified (eg: a DB timestamp, custom logic, ...)
/// </summary>
public interface ILastModifiedInjector
{
    Task<DateTimeOffset> CalculateLastModified(
        ResourceContext context);
}
````

## IDateParser 

Through IDateParser you can inject a custom date parser in case you want to override the default way dates are stringified.  The default implementation (DefaultDateParser) uses the RFC1123 pattern (https://msdn.microsoft.com/en-us/library/az4se3k1(v=vs.110).aspx). 

````csharp
/// <summary>
/// Contract for a date parser, used to parse Last-Modified, Expires, If-Modified-Since and If-Unmodified-Since headers.
/// </summary>
public interface IDateParser
{
    Task<string> LastModifiedToString(DateTimeOffset lastModified);

    Task<string> ExpiresToString(DateTimeOffset lastModified);

    Task<DateTimeOffset?> IfModifiedSinceToDateTimeOffset(string ifModifiedSince);

    Task<DateTimeOffset?> IfUnmodifiedSinceToDateTimeOffset(string ifUnmodifiedSince);
}
````

## IValidatorValueInvalidator

An IValidatorValueInvalidator-implenting class is responsible for marking items for invalidation.

````csharp
/// <summary>
/// Contract for the <see cref="ValidatorValueInvalidator" />
/// </summary>
public interface IValidatorValueInvalidator
{
    /// <summary>
    /// Get the list of <see cref="StoreKey" /> of items marked for invalidation
    /// </summary>
    List<StoreKey> KeysMarkedForInvalidation { get; }

    /// <summary>
    /// Mark an item stored with a <see cref="StoreKey" /> for invalidation
    /// </summary>
    /// <param name="storeKey">The <see cref="StoreKey" /></param>
    /// <returns></returns>
    Task MarkForInvalidation(StoreKey storeKey);

    /// <summary>
    /// Mark a set of items for invlidation by their collection of <see cref="StoreKey" /> 
    /// </summary>
    /// <param name="storeKeys">The collection of <see cref="StoreKey" /></param>
    /// <returns></returns>
    Task MarkForInvalidation(IEnumerable<StoreKey> storeKeys);
}
````

## IStoreKeyAccessor

The IStoreKeyAccessor contains helper methods for getting keys from parts of a URI.  Override this if you're not storing items with their default keys.

````csharp
/// <summary>
/// Contract for finding (a) <see cref="StoreKey" />(s)
/// </summary>    
public interface IStoreKeyAccessor
{
    /// <summary>
    /// Find a  <see cref="StoreKey" /> by part of the key
    /// </summary>
    /// <param name="valueToMatch">The value to match as part of the key</param>
    /// <returns></returns>
    Task<IEnumerable<StoreKey>> FindByKeyPart(string valueToMatch);

    /// <summary>
    /// Find a  <see cref="StoreKey" /> of which the current resource path is part of the key
    /// </summary>
    /// <returns></returns>
    Task<IEnumerable<StoreKey>> FindByCurrentResourcePath();
}
````

BREAKING CHANGE from v7 onwards: the methods return an IAsyncEnumerable<StoreKey> to enable async streaming of results.

````csharp
/// <summary>
/// Contract for finding (a) <see cref="StoreKey" />(s)
/// </summary>    
public interface IStoreKeyAccessor
{
    /// <summary>
    /// Find a  <see cref="StoreKey" /> by part of the key
    /// </summary>
    /// <param name="valueToMatch">The value to match as part of the key</param>
    /// <returns></returns>
    IAsyncEnumerable<StoreKey> FindByKeyPart(string valueToMatch);

    /// <summary>
    /// Find a  <see cref="StoreKey" /> of which the current resource path is part of the key
    /// </summary>
    /// <returns></returns>
    IAsyncEnumerable<StoreKey> FindByCurrentResourcePath();
}
````

