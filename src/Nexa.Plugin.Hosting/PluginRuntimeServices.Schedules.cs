using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private sealed class Scheduled(Entry entry,ScheduleDefinition definition,ScheduleState state)
    {
        internal Entry Entry=entry;
        internal ScheduleDefinition Definition=definition;
        internal ScheduleState State=state;
        internal AbiValue? PendingEvent;
        internal Guid? EventId;
        internal long Sequence;internal bool Saved;
    }
    private readonly Dictionary<QualifiedId,Scheduled> _schedules=[];
    private readonly HashSet<Guid> _runtimeReady=[];
    internal void Activated(PluginIdentity owner)
    {
        lock(_gate)
        {
            _runtimeReady.Add(owner.LoadInstanceId);
            foreach(var sub in _subscriptions.Values.Where(s=>s.Owner==owner&&s.Handler is not null))
            {
                if(sub.Checkpoint is { } checkpoint)
                {
                    var history=_eventHistory[checkpoint.Event];
                    if(history.Events.Count>0&&checkpoint.Sequence<history.Events[0].Sequence-1)sub.Gap=true;
                    foreach(var item in history.Events.Where(e=>e.Sequence>checkpoint.Sequence&&!sub.Pending.ContainsKey(e.Envelope.EventId)))QueueEvent(sub,item.Envelope);
                }
                StartDrains(sub);
            }
        }
    }
    private long _scheduleEventBytes;
    private void SetScheduleInput(Scheduled schedule,AbiValue? input)
    {
        long delta=(input is null?0:AbiValueBinary.Encode(input).Length)-(schedule.PendingEvent is null?0:AbiValueBinary.Encode(schedule.PendingEvent).Length);
        if(_scheduleEventBytes+delta>16*1024*1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        schedule.PendingEvent=input;_scheduleEventBytes+=delta;
    }
    private readonly CancellationTokenSource _scheduleLifetime=new();
    private readonly SemaphoreSlim _schedulePump=new(1,1);
    private Task? _scheduler;
    private IEnumerable<HostCapabilityRoute> ScheduleRoutes => [
        HostRoutes.Typed<ScheduleDefinition,RegistrationRef>("Schedules","Register",(c,v)=>ValueTask.FromResult(RegisterSchedule(c,v))),
        HostRoutes.Typed<PageRequest,Page<ScheduleState>>("Schedules","List",(c,v)=>
        {lock(_gate)return ValueTask.FromResult(Page(c,v,"schedules",_schedules.Values.Where(s=>s.Entry.Owner==c.Identity&&Visible(c,s.Entry)).OrderBy(s=>s.Definition.Id.Value,StringComparer.Ordinal).Select(s=>s.State)));}),
        HostRoutes.Typed<QualifiedRequest,ScheduleState>("Schedules","Enable",(c,v)=>ValueTask.FromResult(EnableSchedule(c,v.Id,true))),
        HostRoutes.Typed<QualifiedRequest,ScheduleState>("Schedules","Disable",(c,v)=>ValueTask.FromResult(EnableSchedule(c,v.Id,false))),
        HostRoutes.Typed<QualifiedRequest,ScheduleState>("Schedules","ReadNextRun",(c,v)=>{lock(_gate)return ValueTask.FromResult(OwnedSchedule(c,v.Id).State);}),
        HostRoutes.Typed<QualifiedRequest,OperationRef>("Schedules","TriggerNow",async(c,v)=>
        {
            Scheduled schedule;lock(_gate){schedule=OwnedSchedule(c,v.Id);if(!schedule.State.Enabled)throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);}
            AbiValue input;lock(_gate){input=schedule.PendingEvent??(schedule.Definition.Trigger==ScheduleTrigger.Event?throw new AbiHostException(AbiErrors.CONTEXT_NOT_READY):Empty.Write(new()));}
            return await FireSchedule(schedule,input,c.Options.OperationId,c.Options.Cancellation,schedule.EventId).ConfigureAwait(false);
        })
    ];
    private Scheduled OwnedSchedule(HostInvocation caller,QualifiedId id)
    {
        if(!_schedules.TryGetValue(id,out var schedule)||!Visible(caller,Find(caller,"schedule",id)))throw new AbiHostException(AbiErrors.NOT_FOUND);
        if(schedule.Entry.Owner!=caller.Identity)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
        return schedule;
    }
    private RegistrationRef RegisterSchedule(HostInvocation caller,ScheduleDefinition definition)
    {
        try{_=TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZone);}catch(Exception e)when(e is TimeZoneNotFoundException or InvalidTimeZoneException){throw new AbiHostException(AbiErrors.SCHEMA_INVALID);}
        if(definition.ReplayLimit is <1 or >32||definition.DebounceMilliseconds is <0 or >300000)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        bool valid=definition.Trigger switch
        {
            ScheduleTrigger.Interval=>definition.IntervalMilliseconds is >=1000 and <=2592000000&&definition.At is null&&definition.Event is null,
            ScheduleTrigger.AtTime=>definition.At is not null&&definition.Event is null&&definition.IntervalMilliseconds is null,
            ScheduleTrigger.Event=>definition.Event is not null&&definition.At is null&&definition.IntervalMilliseconds is null,
            _=>false
        };
        if(!valid)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        lock(_gate)
        {
            if(_schedules.Count>=256)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var state=new ScheduleState(definition.Id,true,InitialDue(definition),NewRevision());
            var restored=ReadSchedule(caller.Identity,definition,state);
            var registration=Stage(caller,"schedule",definition.Id,definition);
            _schedules.Add(definition.Id,new(Find(caller,"schedule",definition.Id),definition,restored.State){Sequence=restored.Sequence});
            return registration;
        }
    }
    private DateTimeOffset? InitialDue(ScheduleDefinition definition)=>definition.Trigger switch
    {
        ScheduleTrigger.Interval=>_options.Clock.GetUtcNow().AddMilliseconds(definition.IntervalMilliseconds!.Value),
        ScheduleTrigger.AtTime=>definition.At!.Value.ToUniversalTime(),_=>null
    };
    private void ValidateSchedules(Guid group)
    {
        lock(_gate)
        foreach(var schedule in _schedules.Values.Where(s=>s.Entry.Group==group))
        {
            var caller=new HostInvocation(schedule.Entry.Owner,group,"Checkup",new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default));
            var workflow=WorkflowDefinition.Read(Find(caller,"workflow",schedule.Definition.Workflow).Value);
            if(schedule.Definition.Trigger==ScheduleTrigger.Event)
            {
                var descriptor=EventDescriptor.Read(Find(caller,"event",schedule.Definition.Event!).Value);
                if(workflow.Input!=descriptor.Schema&&workflow.Input!=Abi100Schemas.EventEnvelope)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            }
            else Validate(workflow.Input,Empty.Write(new()));
        }
    }
    private ScheduleState EnableSchedule(HostInvocation caller,QualifiedId id,bool enabled)
    {
        lock(_gate)
        {
            var scheduled=OwnedSchedule(caller,id);var old=scheduled.State;
            if(old.Enabled==enabled)return old;
            scheduled.State=old with {Enabled=enabled,NextRun=enabled?InitialDue(scheduled.Definition):null,Revision=NewRevision()};
            try{PersistSchedule(scheduled);}catch{scheduled.State=old;throw;}
            SetScheduleInput(scheduled,null);scheduled.EventId=null;_revision=NewRevision();return scheduled.State;
        }
    }
    private void RetireSchedules(Guid instance)
    {foreach(var id in _schedules.Where(p=>p.Value.Entry.Owner.LoadInstanceId==instance).Select(p=>p.Key).ToArray()){SetScheduleInput(_schedules[id],null);_schedules.Remove(id);}}
    internal void StartScheduler()
    {
        if(!_options.AutomaticSchedules)return;
        using(ExecutionContext.SuppressFlow())_scheduler=Task.Run(async()=>
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250),_options.Clock);
            try{while(await timer.WaitForNextTickAsync(_scheduleLifetime.Token).ConfigureAwait(false))await PollSchedulesAsync(_scheduleLifetime.Token).ConfigureAwait(false);}
            catch(OperationCanceledException)when(_scheduleLifetime.IsCancellationRequested){}
        });
    }
    internal async ValueTask StopScheduler()
    {
        await _scheduleLifetime.CancelAsync().ConfigureAwait(false);
        if(_scheduler is not null)await _scheduler.ConfigureAwait(false);
        await _schedulePump.WaitAsync().ConfigureAwait(false);_schedulePump.Release();
    }
    /// <summary>Trusted Host timer port. The automatic pump uses the same path.</summary>
    public async ValueTask PollSchedulesAsync(CancellationToken cancellation=default)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_scheduleLifetime.Token);
        await _schedulePump.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            Scheduled[] schedules;lock(_gate)schedules=_schedules.Values.ToArray();
            foreach(var schedule in schedules)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                try
                {
                    List<(AbiValue Input,Guid Operation,Guid? Cause)> fire=[];
                    lock(_gate)
                    {
                        if(!_schedules.TryGetValue(schedule.Definition.Id,out var current)||!ReferenceEquals(current,schedule)||!schedule.State.Enabled||!_runtimeReady.Contains(schedule.Entry.Owner.LoadInstanceId)||
                            !_entries.TryGetValue(("schedule",schedule.Definition.Id),out var entry)||entry.Registration.State!=RegistrationState.Active)continue;
                        if(!schedule.Saved)PersistSchedule(schedule);
                        var now=_options.Clock.GetUtcNow();if(schedule.State.NextRun is not { } due||due>now)continue;
                        var old=schedule.State;long oldSequence=schedule.Sequence;
                        long count=1;
                        if(schedule.Definition.Trigger==ScheduleTrigger.Interval)count=1+(now.UtcTicks-due.UtcTicks)/TimeSpan.FromMilliseconds(schedule.Definition.IntervalMilliseconds!.Value).Ticks;
                        long deliver=schedule.Definition.Missed switch
                        {MissedSchedulePolicy.Coalesce=>1,MissedSchedulePolicy.ReplayBounded=>Math.Min(count,schedule.Definition.ReplayLimit),_=>count>1||schedule.Definition.Trigger==ScheduleTrigger.AtTime&&now-due>TimeSpan.FromSeconds(1)?0:1};
                        AbiValue input=schedule.PendingEvent??Empty.Write(new());
                        for(int i=0;i<deliver;i++){schedule.Sequence++;fire.Add((input,Guid.NewGuid(),schedule.EventId));}
                        DateTimeOffset? next=null;
                        if(schedule.Definition.Trigger==ScheduleTrigger.Interval)
                        {
                            long advance=checked(count*TimeSpan.FromMilliseconds(schedule.Definition.IntervalMilliseconds!.Value).Ticks);
                            next=due.UtcTicks<=DateTimeOffset.MaxValue.UtcTicks-advance?due.AddTicks(advance):null;
                        }
                        schedule.State=old with {NextRun=next,Revision=NewRevision()};
                        try{PersistSchedule(schedule);}catch{schedule.State=old;schedule.Sequence=oldSequence;throw;}
                        SetScheduleInput(schedule,null);schedule.EventId=null;_revision=NewRevision();
                    }
                    foreach(var item in fire)
                        await FireSchedule(schedule,item.Input,item.Operation,lifetime.Token,item.Cause).ConfigureAwait(false);
                }
                catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){ReportScheduleFailure(schedule,e is AbiHostException known?known.Code:AbiErrors.PROVIDER_UNAVAILABLE);}
            }
        }
        finally{_schedulePump.Release();}
    }
    private async ValueTask<OperationRef> FireSchedule(Scheduled schedule,AbiValue input,Guid occurrence,CancellationToken cancellation,Guid? cause)
    {
        WorkflowDefinition workflow;
        lock(_gate)
        {
            if(!_schedules.TryGetValue(schedule.Definition.Id,out var current)||!ReferenceEquals(current,schedule)||!schedule.State.Enabled||!_runtimeReady.Contains(schedule.Entry.Owner.LoadInstanceId))throw new AbiHostException(AbiErrors.PLUGIN_STOPPED);
            var caller=new HostInvocation(schedule.Entry.Owner,schedule.Entry.Group,"Active",new(occurrence,TimeSpan.FromSeconds(5),cancellation));
            workflow=WorkflowDefinition.Read(Find(caller,"workflow",schedule.Definition.Workflow).Value);
        }
        var options=new InvocationOptions(occurrence,TimeSpan.FromSeconds(5),cancellation){IdempotencyKey="schedule/"+occurrence.ToString("N"),CorrelationId=occurrence,CausationId=cause};
        var result=await _invokeAdmitted(schedule.Entry.Owner,"nexa.workflows/100",4,WorkflowRequest.Write(new(workflow.Id,input,workflow.DefinitionDigest)),options).ConfigureAwait(false);
        if(!result.Success)throw new AbiHostException(result.Error!.Code);return OperationRef.Read(result.Value);
    }
    private void OnScheduledEvent(EventEnvelope envelope)
    {
        foreach(var schedule in _schedules.Values.Where(s=>s.Definition.Trigger==ScheduleTrigger.Event&&s.Definition.Event==envelope.Event&&s.State.Enabled))
        {
            if(!_runtimeReady.Contains(schedule.Entry.Owner.LoadInstanceId)||!_authorize(schedule.Entry.Owner,"nexa.events.read")||!_entries.TryGetValue(("schedule",schedule.Definition.Id),out var entry)||entry.Registration.State!=RegistrationState.Active)continue;
            var caller=new HostInvocation(schedule.Entry.Owner,schedule.Entry.Group,"Active",new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default));
            if(!_entries.TryGetValue(("workflow",schedule.Definition.Workflow),out var workflowEntry)||!Visible(caller,workflowEntry)){ReportScheduleFailure(schedule,AbiErrors.PROVIDER_GONE);continue;}
            var workflow=WorkflowDefinition.Read(workflowEntry.Value);
            try{SetScheduleInput(schedule,workflow.Input==Abi100Schemas.EventEnvelope?EventEnvelope.Write(envelope):envelope.Payload);}
            catch(AbiHostException error){ReportScheduleFailure(schedule,error.Code);continue;}
            schedule.EventId=envelope.EventId;
            schedule.State=schedule.State with {NextRun=_options.Clock.GetUtcNow().AddMilliseconds(schedule.Definition.DebounceMilliseconds),Revision=NewRevision()};
            _revision=NewRevision();
        }
    }
    private void ReportScheduleFailure(Scheduled scheduled,string code)
    {
        lock(_gate)
        {
            if(!_schedules.ContainsKey(scheduled.Definition.Id))return;
            var caller=new HostInvocation(scheduled.Entry.Owner,scheduled.Entry.Group,"Active",new(Guid.NewGuid(),TimeSpan.FromSeconds(1),default));
            try{Log(caller,new(code,Text("schedule.failed"),DiagnosticSeverity.Error,Guid.NewGuid(),null));}catch(AbiHostException){}
        }
    }
    private string SchedulePath(string owner,QualifiedId id)
    {
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner+"/"+id.Value)));
        return Path.Combine(Path.GetDirectoryName(_workflowStore)!,"schedules",key+".schedule");
    }
    private (ScheduleState State,long Sequence) ReadSchedule(PluginIdentity owner,ScheduleDefinition definition,ScheduleState fallback)
    {
        string path=SchedulePath(owner.PluginId,definition.Id);if(!File.Exists(path))return(fallback,0);
        if(new FileInfo(path).Length>65536)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var map=DataCodec.ReadMap(AbiValueBinary.Decode(File.ReadAllBytes(path)));
        if(DataCodec.ReadString(DataCodec.Field(map,"pluginVersion"))!=owner.PluginVersion||!AbiValueBinary.Encode(DataCodec.Field(map,"definition")).SequenceEqual(AbiValueBinary.Encode(ScheduleDefinition.Write(definition))))
            throw new AbiHostException(AbiErrors.PLAN_STALE);
        var state=ScheduleState.Read(DataCodec.Field(map,"state"));if(state.Id!=definition.Id)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        if(definition.Trigger==ScheduleTrigger.Event)state=state with {NextRun=null}; // transient event payload is never invented on restart
        return(state,DataCodec.ReadInt64(DataCodec.Field(map,"sequence")));
    }
    private void PersistSchedule(Scheduled schedule)
    {
        string path=SchedulePath(schedule.Entry.Owner.PluginId,schedule.Definition.Id),directory=Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if(!File.Exists(path)&&Directory.EnumerateFiles(directory,"*.schedule").Take(256).Count()>=256)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var bytes=AbiValueBinary.Encode(DataCodec.Map(("pluginVersion",DataCodec.WriteString(schedule.Entry.Owner.PluginVersion)),("definition",ScheduleDefinition.Write(schedule.Definition)),("state",ScheduleState.Write(schedule.State)),("sequence",DataCodec.WriteInt64(schedule.Sequence))));
        if(bytes.Length>65536)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}File.Move(temporary,path,true);schedule.Saved=true;}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
