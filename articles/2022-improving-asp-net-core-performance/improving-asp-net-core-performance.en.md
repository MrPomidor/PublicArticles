# Simple steps towards boosting ASP NET Core application performance

## Intro
Facing performance-related issues while developing applications is a matter of time. After searching using different data sources, you will find a list of performance advice for concrete situations and the entire application. In this article we will consider performance advice, which aims to boost entire application performance with maximum effect while minimizing development effort. We will test how this advice will affect application performance, calculate performance boost and analyze if there are any pitfalls to point out.

This article will be useful for developers and developer leads striving for high application performance. Also, it will be helpful for experienced developers to be used as a starting point for creating their own ASP NET Core performance checklist or extending an existing one.

## When do we need to optimize ?
Performance is a large and complicated topic. To cover it, we need to address every application aspect: from source code, written by your team, to external libs in use, databases, infrastructure etc. Also, while talking about performance, it will be an omission not to talk about performance measurement. To define system performance level and its behavior under the load, we need performance testing using a variety of monitoring tools.

In any application, from simple CRUD to distributed microservice-based system, there will always be issues, a lot of real (or imaginary) performance bottlenecks and too many things which can go wrong. Talking about performance, what shall we begin with ? Let's narrow a search field by modeling different situations which developers usually face.

According to my experience, the situation when users experience significant discomfort from slow interaction with application is the most common source of performance optimization tasks. Often solving this kind of tasks requires focus on concrete application part. Code changes are very local and rarely affect entire application. Each situation is unique and requires an individual approach. There is no single way to solve these issues, only a common approach - define bottleneck with performance testing, safe initial metrics and make improvements until results become satisfactory.

What are alternative sources of performance optimization tasks ? Let's imagine the sales department reporting an amazing contract with a new partner was signed, and soon your application will get tens of thousands of new active users, or powerful marketing company was planned where expected effect is similar. In this case, we also need some kind of optimization, but it became far more complicated to define a concrete tasks and work focus areas. The tasks could be formed as *"We need to make our entire application more scalable and fast"*. What approach can we apply to solve tasks with such a wide focus area ? Does it mean that we need to optimize each endpoint in our application or at least a dozen of most often called, or do we have some other solution that requires less effort ? Are there any performance recommendations, which will take minimal effort but will generate a significant positive effect for the entire application performance ?

Doing short research on the internet, you will likely find a list of such recommendations for boosting performance for the entire ASP NET Core application. Still, before applying them, you need more knowledge regarding required developer efforts and the predicted effect on your application throughput (highly desirable to have some concrete numbers in percentages or RPS). This is the topic we will highlight in this article.

In this article we will go through next recommendations:
- use the latest version of the framework and dependent libs
- use System.Text.Json for request serialization/deserialization
- use server GC mode
- use async/await
- manually configure threads in thread pool

## What do we need to remember before we start optimization ?
Before performance optimization starts, you need to consider two points.

Firstly, your application should not lose functionality. To protect the application from regression bugs, you need to have integration and end-to-end tests scope in place, confirming that changes did not break anything and the application is working fine. This point is valid for any refactoring.

Secondly, any performance optimizations should rely on metrics. You should record initial results before making changes to be sure you make performance better but not worse. Even simple and, at first glance, predictable changes could lead to unexpected performance degradation, and we will consider case which demonstrates this in the article.

## System under test
For testing different recommendations we will use some kind of cutted CMS system on ASP NET Core with Entity Framework, based on Microsoft test database Adventureworks. This CMS will return goods and orders, which makes it similar to most services that accept requests and return some data from the database. Database and other dependencies will be up and running with `docker compose up -d`. We will use [NBomber](https://medium.com/@anton_shyrokykh/nbomber-as-an-alternative-to-jmeter-for-net-developer-432040b91763) as a load testing tool and `dotnet counters` and PerfView as monitoring tools. All demos and code samples for this application are available on [GitHub](https://github.com/MrPomidor/ASPNetPerfImprovementsDemo).

Worth noting that results which we will get are unique for OS and hardware configuration on which we will run performance testing. Its purpose is to demonstrate the effect and give some relative numbers for being used in estimates and forecasts. For your system, numbers could be different. You should always confirm positive (or negative) effects on a system that is as close to your prod configuration as possible.

## Recommendation 1: Use the latest versions of the framework and libraries
By releasing new versions of .NET framework and related libraries and packages, such as ASP NET Core and Entity Framework Core, Microsoft not only extends existing APIs and introduces new features but also continuously works on performance improvements. Memory consumption optimizations, extending existing APIs with copy-free types support as `Span` or `Memory`, extending support for `ValueTasks` etc. To track optimization-related work, you can visit issues with related tags on GitHub (for example for [ASP NET](https://github.com/dotnet/aspnetcore/labels/area-perf) and [Runtime](https://github.com/dotnet/runtime/labels/optimization)). While reading release notes of each new version, we can point out that the framework becomes faster eventually.

Updating framework version on your project could be fast and safe (just change `TargetFramework` in project configuration and update related NuGet packages version), but it could also be a complicated and unpredictable process, which requires serious code changes, adapting the API of new library versions, fixing bugs which can appear etc. In order to make the decision for updating framework version team lead requires some understanding of what results it will bring and how beneficial it will be from application performance point of view. Let's do some tests and try to answer these questions.

For this test, we will need two API projects with different LTS framework versions (Core 3.1 and NET 6), which will have identical code. We will test two methods:

```csharp
private readonly AdventureWorks _dbContext;
public CustomerController(AdventureWorks dbContext)
{
    _dbContext = dbContext;
}

[HttpGet("orders")]
public async Task<IActionResult> GetOrders(int pageNumber = 1, int pageSize = 100)
{
    var orders = await _dbContext.SalesOrderHeaders.AsQueryable().AsNoTracking()
        .Include(x => x.SalesOrderDetails)
        .OrderBy(x => x.SalesOrderID)
        .Skip((pageNumber - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync();
    return Ok(orders);
}

[HttpGet("products")]
public async Task<IActionResult> GetProducts(int pageNumber = 1, int pageSize = 100)
{
    var products = await _dbContext.Products.AsQueryable().AsNoTracking()
        .OrderBy(x => x.ProductID)
        .Skip((pageNumber - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync();
    return Ok(products);
}
```

In our [load test scenario](https://github.com/MrPomidor/ASPNetPerfImprovementsDemo/tree/master/Solution/FrameworkVersionsComparison) we will have 14 clients (7 for each method) which will send maximum possible number of requests during 3 minutes test. That's how we can see the difference in peak throughput for the current configuration. To become more familiar with NBomber you can visit [the project site](https://nbomber.com/docs/loadtesting-basics).

```csharp
var ordersScenario = ScenarioBuilder.CreateScenario("Orders", getOrdersPageStep)
    .WithWarmUpDuration(TimeSpan.FromSeconds(10))
    .WithLoadSimulations(
        LoadSimulation.NewKeepConstant(_copies: 7, _during: TimeSpan.FromMinutes(3))
    );

var productsScenario = ScenarioBuilder.CreateScenario("Products", getProductsPageStep)
    .WithWarmUpDuration(TimeSpan.FromSeconds(10))
    .WithLoadSimulations(
        LoadSimulation.NewKeepConstant(_copies: 7, _during: TimeSpan.FromMinutes(3))
    );
```

Let's run the test.

![Frameworks comparison bar chart](img/frameworks-bar-chart.PNG)

|Framework|orders (RPS)|orders mean (ms)|products (RPS)|products mean (ms)|all (RPS)|
|---|---|---|---|---|---|
|Core 3.1|23.6|241.09|96.1|63.29|119.7|
|NET 6|35.4|197.29|132.1|52.98|167.5|

As a result, we can see significant performance growth - application could handle **39** percent requests more (with an average response time lower than 1 second). Worth noting that we have tested peak throughput. When testing these methods under normal load with stable RPS, the average response time will not be significantly lower.

|Framework|orders mean (ms)|products mean (ms)|
|---|---|---|
|Core 3.1|292.49|99.61|
|NET 6|238.46|102.17|

Upgrading framework version, as we can see, allows application to handle more requests and support higher throughput. Developer efforts for upgrading framework version could vary from one project to another. But you can make efforts more predictable by doing some preparations, learning libraries being used and reading Microsoft [framework migration guides](https://docs.microsoft.com/en-us/aspnet/core/migration/31-to-60?view=aspnetcore-6.0&tabs=visual-studio). Recommendation for upgrading framework version, as for me, looks pretty viable and corresponds to criteria "minimum effort - significant impact on entire application".

## Recommendation 2: Use System.Text.Json
Serialization code participates in every response, which means it is definitely part of any hot path in the API application. For a long time, Newtonsoft.Json, with all its reach functionality, was used as the default serializer, but starting from ASP NET Core 3.0 it was substituted with System.Text.Json.

If a new, more performant serializer is used by default for all projects starting from ASP NET Core 3.0 (which was released more than three years ago), is it useful to recommend anyone to migrate to System.Text.Json ? Yes, it is. Many applications were written relying on Newtonsoft.Json API, such as hand-written JsonConverters. To save backward compatibility, many developers choose to leave Newtonsoft.Json as the default json serializer and don't perform a migration. Backward compatibility is provided with `Microsoft.AspNetCore.Mvc.NewtonsoftJson` package, which is still popular even for NET 6.

![Microsoft.AspNetCore.Mvc.NewtonsoftJson downloads stats](img/mvc-newtonsoft-json-usage.PNG)

Worth noting that System.Text.Json differs from Netwonsoft.Json more than Core 3.1 differs from NET 6. System.Text.Json has more strict rules by default and also doesn't support some scenarios (you can read more about it [here](https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-migrate-from-newtonsoft-how-to)). Serialization/deserialization code is usually segregated from controllers and application logic and configured in Program.cs/Startup.cs, but if your code does not work with System.Text.Json "out of the box", you could spend significant effort on migration. To make this decision, you need to know how it will affect your application performance and throughput.

For the test, we will need two identical projects on NET 6, which will only differ with default serializer. We will test two API endpoints. First will return the order entity by id, for testing small objects (~1 KB). The second will return a page with 100 order entities for testing performance on large objects (~190 KB). In our [test scenario](https://github.com/MrPomidor/ASPNetPerfImprovementsDemo/tree/master/Solution/SerializerComparison) we will see peak load with 20 parallel clients (10 for each endpoint).

![Serializers comparison bar chart](img/serializers-bar-chart.PNG)

|Serialization Framework|order (RPS)|order mean (ms)|orders (RPS)|orders mean (ms)|all (RPS)|
|---|---|---|---|---|---|
|Newtonsoft.Json|284.4|35.14|50|199.65|334.4|
|System.Text.Json|348.1|28.7|60.3|165.78|408.4|

According to test results, application throughput has grown by **22** percent. As we can see, migration to System.Text.Json can significantly improve application behavior under load. Also worth noting lowered memory consumption and GC pressure (tested with stable RPS, same for both versions):

|Serialization Framework|Allocation rate (MB/sec)|Process working set (MB)|
|---|---|---|
|Newtonsoft.Json|15.599|351.977|
|System.Text.Json|11.693|278.454|

But this is not the limit for System.Text.Json. With code generators feature appearing in NET 6, we are able to make auto-generated serializers for your models to make the performance even better because of the reflection-free approach. You need to create `partial` context class, inherited from `JsonSerializerContext`, point required classes via attributes, then register context in Program.cs/Startup.cs.

```csharp
[JsonSerializable(typeof(SalesOrderHeader))]
[JsonSerializable(typeof(List<SalesOrderHeader>))]
public partial class AdventureWorksContext : JsonSerializerContext
{
}
```

```csharp
public static void Main(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    ...
    builder.Services
        .AddControllers()
        .AddJsonOptions(options => {
            options.JsonSerializerOptions.AddContext<AdventureWorksContext>();
        });
    ...
}
```

However, you should know that using generators has its costs. If for the majority of Newtonsoft.Json functions, you could find analog or workaround in System.Text.Json, for generators most of customization including custom value converters is unavailable (more detailed about limitations could be found [here](https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation-modes?pivots=dotnet-6-0)). Functionality for handling cyclic references is also unavailable when using generators, which we can see even in test scenarios. When using Entity Framework models you could have parent entity that contains a reference to collection of children, when each child contains a reference to parent. In our test scenario we have `OrderHeaderHeader` and `SalesOrderDetail`:

```csharp
[Table("SalesOrderHeader", Schema = "Sales")]
public partial class SalesOrderHeader
{
    ...
    public virtual List<SalesOrderDetail> SalesOrderDetails { get; set; }
}

[Table("SalesOrderDetail", Schema = "Sales")]
public partial class SalesOrderDetail
{
    ...
    public virtual SalesOrderHeader SalesOrderHeader { get; set; }
}
```

To solve this issue you have several options available. The first option is to limit using of navigation properties in models, which mean rewriting application and limiting development convenience. The second option is manually setting null to all references. Both of these options hardly match the "minimum efforts" principle, but let's see if it is worth it.

![Serializers with generators comparison bar chart](img/serializers-bar-chart-2.PNG)

|Serialization Framework|order (RPS)|order mean (ms)|orders (RPS)|orders mean (ms)|all (RPS)|
|---|---|---|---|---|---|
|System.Text.Json|348.1|28.7|60.3|165.78|408.4|
|System.Text.Json (with generators)|370.8|26.94|62.9|158.88|433.7|

Compared with default System.Text.Json we can see that application became **6** more performant. It is up to you to decide if using generators is worth it. But migration from Newtonsoft.Json to System.Text.Json, as for me, is worth considering as a good recommendation.

## Recommendation 3: Use server GC mode
.NET framework has a garbage collection mechanism which, in most cases, frees developers from worrying about memory management in the application. In most cases, until application performance becomes an issue. This relates to how the garbage collection works in .NET. To tell a long story short, we can say that for garbage collection runtime stops application functioning (running threads) and continue only when garbage collection is completed. More actively application allocates objects in memory, more often GC is happening, and more time application is blocked. Time of GC work can be usually measured in percentages from the entire application worktime. What values should be considered normal ? It is hard to say, but most of the sources agree that if GC takes 20 percent of your application worktime, you definitely have some troubles :)

> ### Allocation is cheap… until it is not
> 
> *Konrad Kokosa*

We can distinguish two approaches for optimizing GC work. First - rewrite application code with focus on low memory allocation using such techniques as object pooling, copy-free methods for working with arrays using `Span` or `Memory`, using structs instead of objects whenever it is possible, avoiding boxing/unboxing etc. This approach means a lot of developer effort in rewriting the entire application code, which definitely does not match the topic of this article.

The second approach - configure GC for more optimal work. We have two modes of GC work: workstation and server. I will try to tell the long story short, but if you want to get a full and correct picture, please visit next links: [link 1](https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc), [link 2](https://devblogs.microsoft.com/premier-developer/understanding-different-gc-modes-with-concurrency-visualizer/). In workstation mode there is one priority thread which is doing garbage collection in a single heap. In server mode for each logical CPU (more details on [logical CPU](https://unix.stackexchange.com/questions/88283/so-what-are-logical-cpu-cores-as-opposed-to-physical-cpu-cores)) you have a heap and distinct priority thread doing garbage collection. This means that with the same entire heap size in server mode garbage collection should be performed faster. Also, in server mode, heap usually takes more memory, which makes collecting garbage less intensive than in workstation mode, so we sacrifice application low memory consumption for performance.

To enable server GC mode you need to add next value in the project configuration:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
	<PropertyGroup>
		<TargetFramework>net6.0</TargetFramework>
		<ServerGarbageCollection>true</ServerGarbageCollection>
        ...
	</PropertyGroup>
    ...
</Project>
```

Worth noting that there is a peculiarity in GC mode configuration. [Documentation](https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc) says that for standalone applications default GC mode is workstation, but for hosted applications (ASP NET Core is a hosted application) GC mode is defined by host settings. Unfortunately, I did not find more detailed info about when the host defines GC mode as workstation and when as server. While creating demo ASP NET Core applications for this article GC mode was automatically defined as server. You can see what mode is active by using `System.Runtime.GCSettings.IsServerGC` property in runtime, but I recommend setting GC mode explicitly in configuration.

For testing we will need [two identical API projects](https://github.com/MrPomidor/ASPNetPerfImprovementsDemo/tree/master/Solution/GCModeComparison) on NET 6 which will differ only in `ServerGarbageCollection` setting value. Let's try it out with peak load.

![GC modes comparison bar chart](img/gc-modes-bar-chart.PNG)

|GC Mode|order (RPS)|order mean (ms)|
|---|---|---|
|Workstation|5469.4|9.13|
|Server|8283.3|6.03|

According to test results, server mode is **51** percent more performant than workstation, so we can state - if your web application for some reason is working in workstation mode, server mode will help dramatically increase your application performance with minimum efforts.

## Recommendation 4: Use async/await
In ASP NET Core application one thread could handle one incoming request from the user. The amount of threads in the pool is limited. If the execution thread faces synchronous I/O bound operation, it will just wait, wasting CPU resources. Thread in this situation cannot do any useful work until the operation is completed. If while handling multiple requests most threads will be blocked the same way, application throughput and scalability will be limited. The recommendation in this case is simple - use asynchronous method overloads with async/await. The thread, instead of waiting, will return to the thread pool ready to do business useful job, and your application could handle more requests.

This recommendation, unlike previous ones, means multiple code changes in your application, from API controller method signature to the final point of calling DB using, for example, Entity Framework Core. Development efforts for this kind of refactoring could be significant, including integration testing for reducing regressions. Why are we considering this recommendation in terms of this article ?

Firstly, translating code to async/await is quite simple from my point of view. Translating the call chain to using async/await even could be performed automatically using Roslyn analyzers and code fixes (such analyzers are described in next articles: [article 1](https://www.meziantou.net/enforcing-asynchronous-code-good-practices-using-a-roslyn-analyzer.htm) and [article 2](https://cezarypiatek.github.io/post/async-analyzers-summary/)). To predict the required effort it is enough to run analyzers setting `severity = error` for async-related rules. This kind of refactoring with good preparations and good test coverage could be performed quite fast and safely.

Secondly, this recommendation is still actual. It is not a secret that there exists a lot of projects written a long time ago using the old framework and library versions. This kind of project is still supported. Support tasks for this kind of project also could include increasing responsiveness and performance of the application. During the last two years, I have faced three similar projects where we did such kind of refactoring, also for the purpose of improving application performance.

Before making this decision, it will be helpful for the team lead to know what effect it can potentially have on application throughput. To get this data let's run a peak load test for [two versions of API](https://github.com/MrPomidor/ASPNetPerfImprovementsDemo/tree/master/Solution/SyncAsyncComparison), which consists of two methods: first for returning entity by id, second will be more heavy and returning page of one hundred entities. The first API version will be fully synchronous, and the second will be fully asynchronous.

![Sync vs Async comparison bar chart](img/sync-vs-async-bar-chart.PNG)

|API version|order (RPS)|order mean (ms)|orders (RPS)|orders mean (ms)|all (RPS)|
|---|---|---|---|---|---|
|Sync|1257.3|39.74|460.2|108.57|1717.5|
|Async|2316.3|21.57|409.9|121.89|2725.3|

According to test results, we can see that the entire application throughput was increased by **58** percent, so the recommendation to rewrite application to async/await can be considered viable. But seeing the results, we must pay attention to the method which returns page results. According to NBomber report, its performance became **11** percent lower. By running another test for this method only, we can confirm performance degradation while switching to async/await:

|Method|RPS|Mean (ms)|
|---|---|---|
|Get orders page (Sync)|579.3|172.5|
|Get orders page (Async)|568.7|174.41|

This can teach us that even changes, effect from which seems predictable could lead to unexpected results. It is important to point out that any optimization results should be confirmed by means of load tests and measuring.

## Recommendation 5: Manually configure threads in the thread pool
While testing synchronous API from previous recommendation we also collected some threads-related stats using `dotnet counters`:

|API version|ThreadPool Thread Count|ThreadPool Queue Length|
|---|---|---|
|Sync|34|50|
|Async|31|0|

As we can see, if all operations are synchronous and there are not enough threads in the pool, work starts piling up in the thread pool queue. Thread pool have the ability to extend under some circumstances, and it is highly possible that thread pool will adapt to this load, but it will take some time. Can we somehow affect this situation and set required threads count manually ? Yes, we can, with the help of `ThreadPool` class.

```csharp
const int WorkerThreads = 70;
const int CompletionPortThreads = 70;

if (!ThreadPool.SetMinThreads(WorkerThreads, CompletionPortThreads))
    throw new ApplicationException("Failed to set minimum threads");
```

Worth noting that manual configuration of thread pool size can affect application performance either positively or negatively. By default thread pool automatically manages threads count using various metrics, such as CPU cores count, amount of work in the queue etc. Automatic management should find a balance when threads count is enough to manage application load but not greater to avoid context switching overhead ([documentation](https://docs.microsoft.com/en-us/dotnet/api/system.threading.threadpool.setminthreads?view=net-6.0#remarks)).

As a starting point, we can take threads count involved in synchronous API testing (**34**) and try to increase this amount by adding thread pool queue length from the same test (**50**). Round result number to **70** and perform a test. 

![Sync vs Async with more threads comparison bar chart](img/sync-vs-async-bar-chart-2.PNG)

|API version|order (RPS)|order mean (ms)|orders (RPS)|orders mean (ms)|all (RPS)|
|---|---|---|---|---|---|
|Sync|1257.3|39.74|460.2|108.57|1717.5|
|Async|2316.3|21.57|409.9|121.89|2725.3|
|Sync (SetMinThreads(70, 70))|2336|21.39|428.8|116.32|2764.8|

As a result of manual threads count management, synchronous API performance closely approaches metrics for asynchronous API. We can conclude that in some cases, manual intervention in threads management can help increase application performance without any significant code changes. This approach could be useful when you have no ability to perform async/await refactoring. But you need to use it with great caution after testing in an environment which is as close to production as possible.

## Summary
In this article we have considered several recommendations for improving performance of ASP NET Core applications. We have tested them and collected data on the impact of each recommendation on application performance. This data should help developers and team leads in making decisions about applying these recommendations to their projects. Source code for all demos used in this article, code for load test scenarios and more detailed testing reports are available on [GitHub](https://github.com/MrPomidor/ASPNetPerfImprovementsDemo).

Hope this article was useful for you. In the next article I will consider analyzing latest releases of Entity Framework Core from a performance perspective.

Thank you for your attention !