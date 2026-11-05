// ReSharper disable CheckNamespace

using System.Runtime.CompilerServices;

namespace Masterly.NightShift
{
    /// <summary>
    /// The <see cref="Masterly.NightShift"/> namespace contains high-level types for configuring,
    /// creating and processing background jobs, such as <see cref="GlobalConfiguration"/>,
    /// <see cref="BackgroundJob"/> and <see cref="BackgroundJobServer"/>.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Annotations
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Annotations"/> namespace contains attributes that enable
    /// additional code inspections in design time with JetBrains ReSharper.
    /// </summary>
    /// <remarks>
    /// To enable annotations, open ReSharper options → Code Inspections → Code Annotations 
    /// and add the <see cref="Masterly.NightShift.Annotations"/> namespace to the corresponding list.
    /// </remarks>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Client
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Client"/> namespace contains types that allow you to
    /// customize the background job creation pipeline using the <see cref="IClientFilter"/>,
    /// or define your own creation process by implementing the <see cref="IBackgroundJobFactory"/>
    /// interface.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Common"/> namespace provides base types for background
    /// job filters, such as <see cref="JobFilterAttribute"/>, and some helper classes.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Dashboard"/> namespace contains types that allow you to
    /// restrict an access to the Dashboard UI by implementing the <see cref="IDashboardAuthorizationFilter"/>
    /// interface, as well as customize it by adding new pages, menu items, metrics, routes.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Dashboard.Pages
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Dashboard.Pages"/> namespace contains the <see cref="LayoutPage"/>
    /// class, layout for all the Dashboard UI pages.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Logging
{
    /// <summary>
    /// The Masterly.NightShift.Logging namespaces contain types that allow you to 
    /// integrate Masterly.NightShift's logging with your projects as well as use it 
    /// to log custom messages.
    /// </summary>
    [CompilerGenerated]
    class NamespaceGroupDoc
    {
    }

    /// <summary>
    /// The <see cref="Masterly.NightShift.Logging"/> namespace contains types that allow you to 
    /// integrate Masterly.NightShift's logging with your projects as well as use it 
    /// to log custom messages.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Logging.LogProviders
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Logging.LogProviders"/> namespace contains types for 
    /// supporting most popular logging frameworks to simplify the logging integration 
    /// with your projects.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Server"/> namespace contains types that are responsible
    /// for background processing. You may use them to customize your processing pipeline
    /// by implementing the <see cref="IServerFilter"/> interface or define your own 
    /// continuously-running background processes by implementing the <see cref="IBackgroundProcess"/> 
    /// as well as create completely custom instances of <see cref="BackgroundProcessingServer"/>.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.States
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.States"/> namespace contains types that describe
    /// background job states and the transitions between them. You can implement
    /// custom <see cref="IElectStateFilter"/> or <see cref="IApplyStateFilter"/>
    /// to customize the state changing pipeline, or define your own state by 
    /// implementing the  <see cref="IState"/> interface.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Storage
{
    /// <summary>
    /// The Masterly.NightShift.Storage namespaces contain abstract types like <see cref="JobStorage"/>,
    /// <see cref="IStorageConnection"/> and <see cref="IWriteOnlyTransaction"/> for
    /// querying and modifying the underlying background job storage. 
    /// These types are also used to implement support for other persistent storages.
    /// </summary>
    [CompilerGenerated]
    class NamespaceGroupDoc
    {
    }

    /// <summary>
    /// The Masterly.NightShift.Storage namespaces contain abstract types like <see cref="JobStorage"/>,
    /// <see cref="IStorageConnection"/> and <see cref="IWriteOnlyTransaction"/> for
    /// querying and modifying the underlying background job storage. 
    /// These types are also used to implement support for other persistent storages.
    /// </summary>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}

namespace Masterly.NightShift.Storage.Monitoring
{
    /// <summary>
    /// The <see cref="Masterly.NightShift.Storage.Monitoring"/> provides data transfer objects 
    /// for the <see cref="IMonitoringApi"/> interface. 
    /// </summary>
    /// <remarks>
    /// I have no idea why I placed these types to a separate namespace, they should 
    /// be moved to the parent <see cref="Masterly.NightShift.Storage"/> namespace in version 2.0.
    /// </remarks>
    [CompilerGenerated]
    class NamespaceDoc
    {
    }
}