using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Services;
using VortexCrm.Core.Sync;

namespace VortexCrm.Core;

/// <summary>
/// Todo el CRM de una organización: su base local y los servicios sobre ella. Cada organización tiene su propia base,
/// así un equipo compartido por dos empresas nunca mezcla datos.
/// </summary>
public sealed class CrmWorkspace
{
    private CrmWorkspace(CrmDatabase db, ICrmContext context, TimeProvider clock, IServicioRecordatorios recordatorios)
    {
        Database = db;
        Master = new MasterDataService(db, context, clock);
        Contactos = new ServicioContactos(db, context, clock);
        Oportunidades = new ServicioOportunidades(db, context, clock);
        Embudos = new ServicioEmbudos(db, context, clock);
        Leads = new ServicioLeads(db, context, clock);
        Cotizaciones = new ServicioCotizaciones(db, context, clock);
        Tareas = new ServicioTareas(db, context, clock, recordatorios);
        Clientes = new ServicioClientes(db, context, clock, Contactos, Oportunidades, Cotizaciones, Tareas);
        Cartera = new ServicioCartera(db, context, clock);
        Actividades = new ServicioActividades(db, context, clock);
        Automatizacion = new ServicioAutomatizacion(db, context, clock);
        Equipos = new ServicioEquipos(db, context, clock);
        Audit = new AuditService(db, context, clock);
        SyncStore = new SqliteSyncStore(db);
        SyncHandlers = new CrmSyncHandlers(db, Master, clock, Cotizaciones);
        SyncHandlers.TasksChanged += () => _ = Tareas.ReprogramarAvisosAsync();
    }

    public CrmDatabase Database { get; }
    public MasterDataService Master { get; }
    public ServicioContactos Contactos { get; }
    public ServicioOportunidades Oportunidades { get; }
    public ServicioEmbudos Embudos { get; }
    public ServicioLeads Leads { get; }
    public ServicioCotizaciones Cotizaciones { get; }
    public ServicioTareas Tareas { get; }
    public ServicioClientes Clientes { get; }
    public ServicioCartera Cartera { get; }
    public ServicioActividades Actividades { get; }
    public ServicioAutomatizacion Automatizacion { get; }
    public ServicioEquipos Equipos { get; }
    public AuditService Audit { get; }
    public SqliteSyncStore SyncStore { get; }
    public CrmSyncHandlers SyncHandlers { get; }

    public static async Task<CrmWorkspace> OpenAsync(string databasePath, ICrmContext context, TimeProvider? clock = null,
        IServicioRecordatorios? recordatorios = null, CancellationToken ct = default)
    {
        var db = new CrmDatabase(databasePath);
        await db.MigrateAsync(ct);
        return new CrmWorkspace(db, context, clock ?? TimeProvider.System, recordatorios ?? new SinRecordatorios());
    }
}
