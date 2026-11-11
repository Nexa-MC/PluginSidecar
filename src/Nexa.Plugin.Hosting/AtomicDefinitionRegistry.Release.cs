using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class AtomicDefinitionRegistry
{
    internal bool Release(HostInvocation caller,RegistrationRef reference)
    {
        lock(authority.PublicationGate)
        {
            var definition=_published.Values.Concat(_staged.Values.SelectMany(g=>g.Values)).SingleOrDefault(e=>e.Registration.Handle==reference.Handle);
            if(definition is not null)
            {
                CheckReference(caller,reference,definition.Registration);
                if(_published.Values.Concat(_staged.Values.SelectMany(g=>g.Values)).Any(e=>e.Definition.Id!=definition.Definition.Id&&e.Definition.References.Any(r=>r.Required&&r.Id==definition.Definition.Id))||
                    _content.Values.Any(e=>e.Kind=="overlay"&&OverlayDefinition.Read(e.Value).Target==definition.Definition.Id||e.Kind=="tag"&&TagDefinition.Read(e.Value).Add.Contains(definition.Definition.Id)))throw new AbiHostException(AbiErrors.CONFLICT);
                if(_published.ContainsKey(definition.Definition.Id))
                {
                    _published=_published.Remove(definition.Definition.Id);
                    try{ValidateContent(Guid.Empty);}catch{_published=_published.Add(definition.Definition.Id,definition);throw;}
                    _storedBytes-=EncodedSize(definition.Definition);InvalidateContent([definition.Definition.Registry]);NotifyContent([definition.Definition.Registry]);
                }
                else
                {
                    foreach(var group in _staged.Where(g=>g.Value.ContainsKey(definition.Definition.Id))){group.Value.Remove(definition.Definition.Id);_stagedRevisions[group.Key]=new(Guid.NewGuid().ToString("N"));break;}
                    _storedBytes-=EncodedSize(definition.Definition);
                }
                return true;
            }
            var item=_content.SingleOrDefault(e=>e.Value.Registration.Handle==reference.Handle);
            if(item.Value is null)return false;
            CheckReference(caller,reference,item.Value.Registration);
            bool used=_published.Values.Concat(_staged.Values.SelectMany(g=>g.Values)).Any(e=>e.Definition.Registry==item.Key)||
                _content.Values.Any(e=>e.Registration.Handle!=reference.Handle&&(e.Kind=="tag"&&(TagDefinition.Read(e.Value).Registry==item.Key||TagDefinition.Read(e.Value).Include.Contains(item.Key))||
                    e.Kind=="overlay"&&(OverlayDefinition.Read(e.Value).Before.Contains(item.Key)||OverlayDefinition.Read(e.Value).After.Contains(item.Key))));
            if(used)throw new AbiHostException(AbiErrors.CONFLICT);
            var affected=item.Value.Kind switch
            {
                "registry"=>item.Key,"tag"=>TagDefinition.Read(item.Value.Value).Registry,
                _=>_published.GetValueOrDefault(OverlayDefinition.Read(item.Value.Value).Target)?.Definition.Registry
            };
            _content.Remove(item.Key);
            try{ValidateContent(caller.Phase=="Active"?Guid.Empty:caller.GroupId);}catch{_content.Add(item.Key,item.Value);throw;}
            _storedBytes-=item.Value.Bytes;
            if(item.Value.Registration.State==RegistrationState.Active&&affected is not null){InvalidateContent([affected]);NotifyContent([affected]);}
            else _stagedRevisions[caller.GroupId]=new(Guid.NewGuid().ToString("N"));
            return true;
        }
    }
    internal static void CheckReference(HostInvocation caller,RegistrationRef reference,RegistrationRef actual)
    {
        if(reference.Owner!=caller.Identity.PluginId||reference.LoadInstanceId!=caller.Identity.LoadInstanceId||reference.Handle!=actual.Handle||reference.Owner!=actual.Owner||reference.LoadInstanceId!=actual.LoadInstanceId||reference.DefinitionId!=actual.DefinitionId)
            throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
    }
}
