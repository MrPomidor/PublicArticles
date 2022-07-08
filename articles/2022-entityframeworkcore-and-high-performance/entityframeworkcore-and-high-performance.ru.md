# Entity Framework Core и высокая производительность

Entity Framework Core является рекомендованным и самым популярным средством взаимодействия с реляционными базами данных на платформе ASP NET Core. Это мощный инструмент который подходит для большинства сценариев, но, как и любой другой инструмент имеет свои ограничения. Долгое время бытовало мнение (и не безосновательно) что Entity Framework не подходит для высоконагруженных систем и в таких сценариях лучше использовать Dapper. Но время идет и Entity Framework развивается, в том числе в плане оптимизации. Помимо улучшения производительности самой платформы .NET, Entity Framework Core для NET 6 имеет ряд настроек и возможностей, призванных значительно улучшить производительность. В этой статье мы рассмотрим Entity Framework Core с точки зрения производительности и сравним его с Dapper используя актуальные версии на момент июля 2022 года. Посмотрим насколько рекомендация "перепишите все на Dapper" актуальна :)

Эта статья будет полезна разработчикам, которые используют Entity Framework Core в ежедневной работе, а также разработчикам высоконагруженных систем для актуализации знаний о возможностях последних версий Entity Framework Core. 

## Введение в EF
Перед углублением в тему производительности было бы полезно вспомнить что такое EF и описать некоторые аспекты его работы, которые помогут нам в понимании разных подходов к оптимизации. Итак, EF это object-relational mapper (ORM) или инструмент, связывающий объектную модель, с которой мы работаем в коде (C# классы, коллекции, свойства) с реляционной моделью базы данных (таблица, столбец, запись, связи etc). Основной объект, который предоставляет EF для работы с базой данных это класс производный от `DbContext`. Класс содержит в себе набор объектов-коллекций `DbSet`, которые чаще всего соотносятся с таблицами базы данных. Для доступа к этим данным, мы обращаемся к этим коллекциям с помощью LINQ запросов, которые за кадром транслируются в SQL при вызове методов `ToArray`, `ToList`, `FirstOrDefault` и т.д., и работаем с данными также, как и с обычными C# объектами.   

```csharp
public class AdventureWorksContext : DbContext
{
    public virtual DbSet<Product> Products { get; set; }
    ...
}
...
public void ApplicationLogic()
{
    using var context = new AdventureWorksContext();
    // get list of products with filter
    var bookProducts = context.Products.Where(p => p.Type == "Book").ToList();
    // get single product by name
    var singleBook = context.Products.Where(p => p.Name == "Harry Potter").FirstOrDefault();
    // do data handling
    ...
}
```

То, как работает, EF дает разработчикам несколько преимуществ. Во-первых, EF берет на себя ответственность за формирование корректных и безопасных от SQL инъекций запросов к базе данных для конкретного провайдера, используя строго типизированные LINQ запросы. Один и тот же C# код будет работать с MSSQL, Oracle и MySQL. Разработчик в большинстве случаев полностью абстрагируется от работы с SQL синтаксисом и может сосредоточиться на логике приложения. Во-вторых, EF предоставляет механизм, который отслеживает изменения свойств объектов (change-tracking) и позволяет при фиксации формировать Update и Delete запросы в базу данных, также без написания разработчиком какого либо SQL кода. Например: 

```csharp
public void ApplicationLogic()
{
    using var context = new AdventureWorksContext();
    var bookProduct = context.Products.Where(p => p.Name == "Harry Potter").Single();
    bookProduct.Name = "Harry Potter and the Sorcerer's Stone"
    context.SaveChanges();
    // name in DB was updated ! 

    context.Remove(bookProduct);
    context.SaveChanges();
    // book was deleted from DB !
    ...
}
```

EF имеет богатый функционал, значительно облегчающий разработку, однако это имеет свою цену и каждый этап обработки перед отправкой SQL запроса в базу данных и после получения ответа требует ресурсов. Попробуем составить упрощенную поэтапную схему работы EF от написания LINQ запроса, до получения данных.

1. Получение экземпляра `DbContext`. Для начала нам необходимо получить экземпляр `DbContext`, который содержит все что необходимо для работы.
2. Компиляция LINQ запроса в SQL. Реализация интерфейса `IQueryable`, которую мы получаем вызывая методы расширения LINQ на `DbSet`, представляет из себя объект запроса, который предстоит выполнить. Объект строится по принципу builder-а: каждый вызванный метод `Where`, `OrderBy`, `Select` и т.д. добавляет в объект запроса новую информацию, которая позже будет транслирована в SQL. `IQueryable` наследуется от `IEnumerable`, но до тех пор, пока вы не вызвали методы `IEnumerable` (или `IAsyncEnumerable`) явно - SQL запрос не будет сформирован и отправлен в базу. При вызове на `IQueryable` объекте метода, явно приводящего запрос к `IEnumerable` (или `IAsyncEnumerable`), такого как `ToArray`, `ToList`, `FirstOrDefault` и т.д. запускается процесс трансляции. EF транслирует объект `IQueryable` в SQL, при этом поддерживая внутренний кэш, который позволяет переиспользовать результаты трансляции для одинаковых LINQ запросов и не проводить тяжелые вычисления повторно.
3. Отправка SQL запроса в базу данных и получение ответа. (server-side calculations)
4. Материализация результатов запроса в C# объекты.
5. Регистрация объектов в системе отслеживания изменений (change-tracking). После того как произошла материализация объектов, EF по умолчанию регистрирует эти объекты во внутренней системе отслеживания изменений, которая отслеживает изменения свойств объекта и при вызове `SaveChanges` формирует соответствующий Update запрос в базу данных. На поддержание системы отслеживания изменений и информации которая в ней хранится также тратятся ресурсы.
6. Выполнение клиентской части LINQ запроса (client-side calculations). Получая LINQ запрос EF пытается трансформировать его в SQL чтобы он был выполнен на сервере базы данных (server-side calculation). Но в некоторых случаях он не может этого сделать, и тогда выражение должно быть рассчитано на клиенте, после того как все что удалось трансформировать в SQL выполнится на сервере. В ранних версиях EF Core программист мог узнать о том что EF не смог трансформировать часть запроса в SQL и она была неявно выполнена на клиенте только из логов, или специально настроив выброс исключений в подобных случаях, однако в последних версиях, при невозможности конвертировать LINQ в SQL EF всегда будет выбрасывать исключение, требуя явного вызова методов `AsEnumerable`, `ToList` и т.д. перед частью, которая может быть рассчитана только в C# (client-side calculations). Более подробно это описано в [этой статье Microsoft](https://docs.microsoft.com/en-us/ef/core/querying/client-eval). В интересах разработчика чтобы как можно больше вычислений, в особенности в блоке `Where`, происходили на стороне SQL сервера.
7. Получение объектов вызывающим кодом.

Как видим между созданием `DbContext`, вызовом ADO NET и получением результатов в коде выполняется множество операций, которые потребляют ресурс процессора, создают объекты и сохраняют ссылки на них, нагружая GC, заполняют и очищают внутренние кэши и т.д. Dapper в свою очередь представляет собой минимальную прослойку между ADO NET и клиентским кодом, лишенную всех преимуществ EF, но от этого имеющую значительное преимущество по производительности. Для наглядности приведем пример кода с использованием Dapper:

```csharp
public void ApplicationLogic()
{
    using var sqlConnection = new SqlConnection(connectionString);
    var product = connection.QuerySingleOrDefault<Product>(@"
        select * from Products 
        where Name = @name
    ", new { name = "Harry Potter" });
}
```

Теперь, когда мы лучше представляем как работает EF и где будет происходить оптимизация, мы можем перейти к обзору системы, производительность которой мы будем улучшать.

## System Under Test (SUT)
Для демонстрации и сравнения нам понадобится веб API, которое будет взаимодействовать с тестовой SQL базой AdventureWorks, реализуя несколько часто встречающихся сценариев:
- GET запрос по Id с данными из одной таблицы. *Get product by Id*
- GET запрос по Id с данными из нескольких связанных таблиц (JOIN-s). *Get product with model and product category by id*
- GET запрос страницы с данными из одной таблицы. *Get products page*
- GET запрос страницы с данными из нескольких связанных таблиц (JOIN-s). *Get products page with model and product category datas*
- POST запрос на создание. *Create product*
- PUT запрос на редактирование. *Edit product name*

Нам понадобится реализовать API несколько раз, используя разные имплементации `IProductsRepository` на базе EF или Dapper для доступа к данным. Для полноценного нагрузочного тестирования мы будем использовать NBomber поочередно для всех перечисленных сценариев. Подробнее о NBomber и работе с ним можно ознакомится в [этой статье](https://habr.com/ru/post/664824/). Для более быстрых локальных тестов в некоторых случаях мы будем использовать [BenchmarkDotNet сценарии](https://github.com/MrPomidor/EFCorePerformanceTipsDemo/tree/master/src/Solution/Tests/Benchmarks), которые будут повторять наше API в миниатюре, вызывая разные реализации интерфейса `IProductsRepository` для EF, Dapper и вариаций EF с различными улучшениями:

```csharp
private ServiceProvider EFCoreDefaultImplementationServiceProvider;
...
[GlobalSetup]
public void GlobalSetup()
{
    BuildDefaultImplementationServiceProvider();
}
...
[Benchmark]
public async Task GetProduct_Benchmark()
{
    // we will do several iterations, emulating several requests, to see difference in time and memory better
    for (int i = 0; i < IterationsCount; i++)
    {
        // as for each HTTP request in web api, we will create DI scope
        using var scope = EFCoreDefaultImplementationServiceProvider.CreateScope();
        // ... from which we will resolve implementation under test 
        var repository = scope.ServiceProvider.GetRequiredService<IProductsRepository>();
        // get product by id scenario as example
        var product = await repository.GetProduct(ProductIds[i % (ProductIds.Length - 1)]);
    }
}
```

После того как мы рассмотрим все [рекомендации](https://docs.microsoft.com/en-us/ef/core/performance/advanced-performance-topics) по улучшению производительности работы EF, мы проведем еще один NBomber тест с примененными улучшениями и после сможем сделать выводы. Весь код использованный в данной статье доступен в [репозитории на Github](https://github.com/MrPomidor/EFCorePerformanceTipsDemo).

Перед началом улучшений проведем замер для Dapper и версии EF "из коробки". Для теста запустим поочередно обе версии приложения и проведем последовательное нагрузочное тестирование для каждого из сценариев, используя 30 тестовых клиентов, безостановочно шлющих запросы.

![EF Default and Dapper](img/efdefault_dapper_barchart.png)

|Scenario|EF Default (RPS)|Dapper (RPS)|
|---|---|---|
|Get product by Id|7124,1|8478,0|
|Get detailed product by Id|6180,5|7439,9|
|Get products page|3320,7|4341,2|
|Get detailed products page|1174,8|954,8|
|Create product|2146,2|3967,9|
|Edit product|1859,8|4371,7|

Как видим в данной конфигурации EF на **19-30** процентов уступает Dapper в большинстве сценариев для чтения, и значительно уступает в сценариях создания и редактирования. Теперь мы имеем точку отсчета и можем приступить к работе над улучшениями.

## DbContext pooling
Для повышения производительности при работе с EF нам необходимо постепенно уменьшать влияние промежуточных этапов которые мы описали ранее, уменьшая количество аллокации, повторных вычислений и по возможности делая часть вычислений наперед (pre-calculation). Microsoft [предлагает](https://docs.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#dbcontext-pooling) использовать пул для объектов типа `DbContext`. Плюсы этого решения очевидны - переиспользование "тяжелых" объектов уменьшат давление на GC что будет заметно при интенсивной нагрузке. Также среди плюсов стоит отметить легкость в конфигурации - для настройки пулинга вам необходимо поменять лишь одну строку в конфигурации приложения, заменив вызов `AddDbContext` на `AddDbContextPool` в Program.cs. Ваш код доступа к данным (в нашем случае реализация `IProductsRepository`) останется нетронутым. Однако стоит учитывать что ваш `DbContext` по сути становится синглтоном и не должен сохранять никакого состояния между использованиями. Тем не менее если у вас возникает необходимость работать с данными scoped контекста, способ это сделать был [предусмотрен и описан](https://docs.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#managing-state-in-pooled-contexts) разработчиками EF. Также важно предусмотреть достаточно большой размер пула, так как при превышении его размера будут создаваться новые экземпляры `DbContext`.

```csharp
public static void AddEfCore(this IServiceCollection services, IConfiguration config)
{
    //services.AddDbContext<AdventureWorksContext>((dbContextConfig) =>
    services.AddDbContextPool<AdventureWorksContext>((dbContextConfig) =>
    {
        dbContextConfig.UseSqlServer(config.GetConnectionString(ConnectionStringName));
    });

    services.AddScoped<IProductsRepository, EFCoreProductsRepository>();
}
```

## Отключение отслеживания изменений в объектах для read-only запросов
Рассматривая особенности работы EF мы упоминали [систему отслеживания изменений](https://docs.microsoft.com/en-us/ef/core/querying/tracking). Change-tracking позволяет нам обновлять данные трансформируя изменения свойств объектов в SQL Update операции. Эта система включена по умолчанию для всех запросов, однако она имеет смысл только тогда, когда мы собираемся что-то редактировать. В сценариях только для чтения, эта система только создает дополнительные расходы. К счастью, ее можно отключить для конкретного запроса, вызвав метод `AsNoTracking`.

```csharp
public async Task<Product> GetProduct(int productId, CancellationToken cancellationToken = default)
{
    return await _context.Products
        .AsNoTracking()
        .FirstOrDefaultAsync(x => x.ProductId == productId, cancellationToken);
}
```

За пару лет я завел себе привычку всегда писать запросы через `AsNoTracking`, потому что запросы только для чтения приходится писать чаще чем запросы для редактирования. Однако если такой привычки у вас нет то вам необходимо будет выполнить некий объем работы, чтобы проанализировать свой код доступа к данным, выделить запросы только для чтения, добавить `AsNoTracking` и провести тестирование, чтобы убедится что никакие сценарии редактирования не сломались.

Стоит также добавить что поведение запросов по умолчанию в EF можно настроить таким образом, что все запросы будут повторять поведение `AsNoTracking` без явного вызова этого метода. Настроить это можно в месте вызова `AddDbContext`. Тогда вам наоборот придется явно добавлять вызов метода `AsTracking` в тех сценариях, где необходимо что-то отредактировать.

```csharp
public static void AddEfCore(this IServiceCollection services, IConfiguration config)
{
    services.AddDbContext<AdventureWorksContext>((dbContextConfig) =>
    {
        ...
        dbContextConfig.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    });
}

public void ApplicationLogic()
{
    using var context = new AdventureWorksContext();
    var bookProduct = context.Products.AsTracking().Where(p => p.Name == "Harry Potter").Single();
    bookProduct.Name = "Harry Potter and the Sorcerer's Stone"
    context.SaveChanges();
}
```

## Использование DbContext.Entry для редактирования
Отдельно стоит выделить и разобрать результат тестирования сценария редактирования (Edit product), в котором Dapper превосходит EF более чем в 2 раза. Такой результат очень просто объяснить, взглянув на код редактирования в версии `IProductsRepository` для EF:

```csharp
...
var bookProduct = dbContext.Products.Where(p => p.Name == "Harry Potter").Single(); // < -- 1-st query to db
bookProduct.Name = "Harry Potter and the Sorcerer's Stone"
context.SaveChanges(); // <-- 2-nd query to DB
...
```

Для выполнения редактирования с помощью C# нам необходимо сначала получить объект, выполнив запрос в базу данных, модифицировать его и вызвать `SaveChanges`, что отправит еще один запрос в базу данных. Двукратное превосходство Dapper объясняется тем, что EF для редактирования с использованием C# необходимо отправлять в 2 раза больше запросов. Однако в EF есть еще один способ редактирования с использованием C#, который позволяет выполнить всего один запрос. Для этого нам необходимо вручную создать экземпляр `Product`, присвоить ему нужные свойства и вручную отредактировать состояние объекта в системе отслеживания изменений, при необходимости выбирая только те свойства, которые мы хотим поменять. В нашем случае мы собираемся менять только название:

```csharp
public async Task EditProductName(int productId, string productName)
{
    var product = new Product { ProductId = productId, Name = productName };

    _context.Products.Attach(product);
    _context.Entry(product).Property(x => x.Name).IsModified = true;

    try
    {
        _ = await _context.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
        // exception is throws when @@ROWCOUNT is equal to 0
        // which means no entity with such Id was updated
        throw new ProductNotFoundException();
    }
}
```

## Пре-компиляция LINQ выражений в SQL
Важной частью работы EF является процесс трансформации LINQ кода, который пишет C# программист, в SQL запрос, который будет отправлен в базу данных. Компиляция запросов это часто выполняемая операция, поэтому она может рассматриваться как цель для оптимизации. Чтобы избежать трансформации LINQ в SQL во время работы приложения, разработчиками EF предусмотрен механизм пре-компиляции LINQ кода в потокобезопасный делегат, для которого все трансформации уже выполнены и который можно поместить в статическую переменную для переиспользования в приложении. Для создания такого делегата вам необходимо передать в статический метод `EF.CompileQuery`/`EF.CompileAsyncQuery` ваш LINQ код, передавая также все внешние переменные, используемые вашим LINQ кодом, как параметры метода. В результаты вы получите делегат типа `Func<TDbContext, TParameter1, ..., TResult>`, который вы сможете вызывать, не тратя ресурсы на трансляцию LINQ в SQL.

```csharp
private readonly AdventureWorksContext _context;
...
private static Func<AdventureWorksContext, int, CancellationToken, Task<Product>> _getProductByIdQuery =
    EF.CompileAsyncQuery<AdventureWorksContext, int, Product>((ctx, productId, ct) =>
        ctx.Products.AsQueryable().FirstOrDefault(x => x.ProductId == productId));
...
public async Task<Product> GetProduct(int productId, CancellationToken cancellationToken = default)
{
    return await _getProductByIdQuery.Invoke(_context, productId, cancellationToken);
}
```

Несмотря на ожидаемые преимущества от применения такого подхода, а именно уменьшение аллокаций и уменьшение использования CPU, стоит отметить и недостатки. Во-первых, как можно заметить из примера, код стал значительно менее удобен для чтения. Во-вторых, для использования этого подхода вам необходимо затратить значительно больше времени чем на добавление `AsNoTracking`, особенно для переписывания и тестирования уже существующего кода. Отдельно хотелось бы отметить, на мой взгляд, не очень подробную документацию данной возможности и немного запутанный интерфейс метода `EF.CompileAsyncQuery`. С имеющейся документацией можно ознакомиться по [этой ссылке](https://docs.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#compiled-queries).

## Отключение внутренних проверок потокобезопасности
`DbContext` в Entity Framework Core, в отличие от версии для Framework, не поддерживает сценарии работы с несколькими потоками. Для поддержки этого ограничения в EF присутствуют внутренние проверки, которые обнаруживают доступ из нескольких потоков и с помощью понятного исключения уведомляют программиста о неправильном использовании. Однако когда ваше приложение многократно проверено в проде, вы полностью уверены, что ошибок с многопоточностью у вас нет и вы используете `DbContext` правильно, стоит ли рассматривать эти проверки как накладные расходы, которые можно сократить ? Все рассмотренные выше рекомендации могут создавать определенный дискомфорт при разработке и имеют свои ограничения, однако ни одна из них не ставит под угрозу корректность и работоспособность EF. Отключение кода проверяющего корректное использование `DbContext` может иметь непредсказуемые последствия, о чем прямо предупреждается в документации к EF:

> **WARNING**: Only disable thread safety checks after thoroughly testing that your application doesn't contain such concurrency bugs.

Однако в контексте данной статьи и перечисления способов повышения производительности для EF стоит упомянуть что возможность отключить проверки потокобезопасности в `DbContext` есть. Для этого нужно вызвать соответсвующий метод в месте вызова `AddDbContext`:

```csharp
public static void AddEfCore(this IServiceCollection services, IConfiguration config)
{
    services.AddDbContextPool<AdventureWorksContext>(
        dbContextConfig =>
        {
            ...
            dbContextConfig.EnableThreadSafetyChecks(enableChecks: false);
        });
    ...
}
```

Данная конфигурация, так же как и другие, была проверена с помощью BenchmarkDotNet, однако из всех опробованных улучшений показала минимальное влияние на производительность. К сожалению, цифру в **5** процентов прироста производительности, указанную в [одной из issue на Github](https://github.com/dotnet/efcore/pull/24125#issuecomment-777780033), мне повторить не удалось. Применять ли эту опцию в ваших продуктах - решать вам.

|Scenario name|EF Default (ms)|EF Disable concurrency check (ms)|EF Context Pooling (ms)|EF Context Pooling and Disable concurrency check (ms)|
|---|---|---|---|---|
|Create|2,015.9|2,031.8|1,867.9|1,876.7|
|Edit|2,404.7|2,400.6|2,245.6|2,258.2|
|Get by Id|1,067.5|1,055.8|859.2|886.6|
|Get by Id full|1,186.6|1,246.2|984.8|973.0|
|Get page|8,752|8,426|8,105|8,102|
|Get page full|3,413|3,429|3,368|3,394|

## Влияния комбинирования улучшений на производительность
Мы рассмотрели основные рекомендации по повышению производительности EF от Microsoft, разобрали механизм их работы, а также возможные накладные расходы при применении. Приведем общий список рекомендаций:
- Используйте `DbContext` pooling
- Используйте `AsNoTracking` для запросов только для чтения
- Применяйте пре-компилированные в SQL запросы
- Отключайте проверки на потокобезопасность (помня о рисках)

Пришло время их скомбинировать и провести повторное тестирование нашей системы.

![EF Default, EF Improved and Dapper](img/efdefault_efimproved_dapper_barchart.png)

|Scenario|EF Default (RPS)|EF Improved (RPS)|Dapper (RPS)|
|---|---|---|---|
|Get product by Id|7124,1|8354,3|8478,0|
|Get detailed product by Id|6180,5|7297,5|7439,9|
|Get products page|3320,7|4165,5|4341,2|
|Get detailed products page|1174,8|1306,2|954,8|
|Create product|2146,2|2279,1|3967,9|
|Edit product|1859,8|2472,0|4371,7|

Согласно результатам тестирования всех трех версий приложения, мы видим что улучшения для EF позволили на **6-25** процентов улучшить результаты по сравнению с версией EF "из коробки". Также значительно сократился разрыв с Dapper и теперь Dapper превосходит EF в среднем на **1.5-4.2** процента в большинстве сценариев на чтение.

К сожалению, мы также увидели что получить схожую с Dapper производительность для сценариев с редактированием и созданием, при этом сохраняя изоляцию C# программиста от SQL кода, увы не выйдет. Dapper все еще превосходит EF на **76** процентов в редактировании и на **74** процента в создании. Однако стоит отметить что EF конечно же дает программисту возможность вручную писать SQL код с помощью `DbContext.Database.ExecuteSqlRaw`. Таким образом вы сможете оптимизировать узкое место, не подключая при этом сторонних библиотек кроме EF. Результаты бенчмарка показывают что производительность EF `ExecuteSqlRaw` почти идентична коду, написанному на Dapper для обоих сценариев:

```csharp
public async Task EditProductName(int productId, string productName)
{
    var rowsAffected = await _context.Database.ExecuteSqlRawAsync(@"UPDATE [Production].[Product]
        SET [Name] = {0}
        WHERE [ProductID] = {1}
        SELECT @@ROWCOUNT", productName, productId);
    ...
}
```

|Benchmark name|Mean (ms)|Allocated (MB)|
|---|---|---|
|Edit_Default|2,404.7|70|
|Edit_CombinedImprovements|1,780.5|21|
|Edit_ContextPoolingRawSql|940.6|4|
|Edit_Dapper|916.6|3|

```csharp
public async Task<int> CreateProduct(Product product)
{
    var productId = await _context.Database.ExecuteSqlRawAsync(@"INSERT INTO [Production].[Product]
        (Name, ProductNumber, SafetyStockLevel, ReorderPoint, StandardCost, ListPrice, Class, Style, Color, SellStartDate, DaysToManufacture)
    VALUES
        ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10})
    SELECT CAST(SCOPE_IDENTITY() as int)",
    product.Name, product.ProductNumber, product.SafetyStockLevel, product.ReorderPoint, product.StandardCost, product.ListPrice, 
    product.Class, product.Style, product.Color, product.SellStartDate, product.DaysToManufacture);

    return productId;
}
```

|Benchmark name|Mean (ms)|Allocated (MB)|
|---|---|---|
|Create_Default|2,015.9|77|
|Create_ContextPoolingRawSql|947.9|9|
|Create_Dapper|941.3|7|

Стоит также добавить что в [дорожной карте](https://docs.microsoft.com/en-us/ef/core/what-is-new/ef-core-7.0/plan#performance-of-database-inserts-and-updates) для следующей версии EF планируется провести оптимизацию change-tracking механизма и улучшить производительность сценариев Insert и Update:

> For EF7, we plan to focus on performance related to database inserts and updates. This includes performance of change-tracking queries, performance of `DetectChanges`, and performance of the insert and update commands sent to the database.

Мы можем следить за ходом разработки на [Github](https://github.com/dotnet/efcore/issues/26797) и надеятся что со следующим релизом разрыв с Dapper в этих сценариях будет существенно сокращен.

## Итоги
Как мы смогли увидеть, EF Core на момент июля 2022 года при правильном использовании может показывать результаты сопоставимые с Dapper для большинства сценариев для чтения, при этом сохраняя свои преимущества в виде генерирования корректного и безопасного SQL кода, используя строго типизированные LINQ выражения. Пока что EF все еще значительно уступает Dapper в Insert и Update сценариях при использовании C# обьектов для редактирования, но у разработчиков есть возможность при необходимости повысить производительность при помощи raw sql подхода. Мы можем ожидать уменьшение разрыва между EF и Dapper в этих сценариях уже в следующем релизе. На мой взгляд, и как показывает практика, EF Core последней версии вполне применим для использования в высоконагруженных системах. Учитывая богатый функционал, поддержку и популярность, а также то что EF Core и платформа NET не стоят на месте и с каждым релизом становятся лучше в плане производительности, вы не ошибетесь выбрав для разработки EF Core. Надеюсь что статья была вам полезна.

Спасибо за внимание !

Антон Широких