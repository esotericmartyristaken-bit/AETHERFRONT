using System;
using System.Collections;
using System.Reflection;

namespace Aetherfront.SessionPolicy
{
    // No Parallax assembly reference: Potato and fresh starters must still load.
    internal sealed class ParallaxState
    {
        private const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private readonly FieldInfo globals,settings,level,bodies,scatters,bodyColliders,enabled,index;
        private readonly FieldInfo managerInstance,managerScatters,managerCount,objects,data,incoming,outgoing,quadJob,colliderJob;
        private readonly MethodInfo cleanup;
        private readonly Array emptyScatters;

        internal ParallaxState(Type loader,Type manager)
        {
            globals=Field(loader,"parallaxGlobalSettings",true);
            settings=Field(globals.FieldType,"scatterGlobalSettings",false);
            level=Field(settings.FieldType,"collisionLevel",false);
            if(level.FieldType!=typeof(int))throw new InvalidOperationException("Changed Parallax collision setting type");
            bodies=Field(loader,"parallaxScatterBodies",true);
            Type body=bodies.FieldType.GetGenericArguments()[1];
            scatters=Field(body,"scatters",false);bodyColliders=Field(body,"collideableScatters",false);
            Type scatter=scatters.FieldType.GetGenericArguments()[1];
            enabled=Field(scatter,"collideable",false);index=Field(scatter,"collideableArrayIndex",false);
            if(enabled.FieldType!=typeof(bool)||index.FieldType!=typeof(int)||bodyColliders.FieldType!=scatter.MakeArrayType())throw new InvalidOperationException("Changed Parallax scatter schema");
            managerInstance=Field(manager,"Instance",true);managerScatters=Field(manager,"collideableScatters",true);
            managerCount=Field(manager,"numCollideableScatters",true);objects=Field(manager,"activeObjects",true);
            data=Field(manager,"collisionData",true);incoming=Field(manager,"incomingData",true);outgoing=Field(manager,"outgoingData",true);
            quadJob=Field(manager,"inQuadJob",true);colliderJob=Field(manager,"inColliderJob",true);
            cleanup=manager.GetMethod("Cleanup",Instance,null,Type.EmptyTypes,null);
            if(cleanup==null||cleanup.ReturnType!=typeof(void)||managerCount.FieldType!=typeof(int)||managerScatters.FieldType!=scatter.MakeArrayType())throw new InvalidOperationException("Changed Parallax collision manager schema");
            emptyScatters=Array.CreateInstance(scatter,0);
        }
        private static FieldInfo Field(Type type,string name,bool isStatic)
            =>type.GetField(name,isStatic?Static:Instance)??throw new MissingFieldException(type.FullName,name);
        private static int Count(object value,string member)
        {
            if(value==null)return 0;Type t=value.GetType();
            object n=t.GetField(member,Instance)?.GetValue(value)??t.GetProperty(member,Instance)?.GetValue(value,null);
            if(!(n is int))throw new InvalidOperationException("Unknown Parallax collision collection");
            return (int)n;
        }
        internal int Level
        {
            get{object root=globals.GetValue(null);object value=root==null?null:settings.GetValue(root);if(value==null)throw new InvalidOperationException("Parallax settings not initialized");return (int)level.GetValue(value);}
        }
        internal bool ForceLevelOff()
        {
            object root=globals.GetValue(null);
            bool changed=ForceLevelOff(root);globals.SetValue(null,root);return changed;
        }
        internal bool ForceLevelOff(object root)
        {
            object value=root==null?null:settings.GetValue(root);
            if(value==null)throw new InvalidOperationException("Parallax settings not initialized");
            bool changed=(int)level.GetValue(value)!=-1;level.SetValue(value,-1);
            // scatterGlobalSettings is a struct in the shipped mod. Write the
            // modified boxed value back instead of changing a discarded copy.
            settings.SetValue(root,value);return changed;
        }
        internal bool HasWork
        {
            get{
                if((int)managerCount.GetValue(null)!=0||((Array)managerScatters.GetValue(null))?.Length>0||
                   (bool)quadJob.GetValue(null)||(bool)colliderJob.GetValue(null)||Count(data.GetValue(null),"Length")>0||
                   Count(incoming.GetValue(null),"Count")>0||Count(outgoing.GetValue(null),"Count")>0)return true;
                var entries=objects.GetValue(null) as IEnumerable;if(entries!=null)foreach(object entry in entries)if(entry is IDictionary map&&map.Count>0)return true;
                return false;
            }
        }
        internal bool CachedScattersOff
        {
            get{
                var planets=bodies.GetValue(null) as IDictionary;
                if(planets==null)throw new InvalidOperationException("Parallax body registry unavailable");
                foreach(object body in planets.Values){
                    if(body==null)throw new InvalidOperationException("Invalid Parallax body");
                    if(((Array)bodyColliders.GetValue(body))?.Length>0)return false;
                    var entries=scatters.GetValue(body) as IDictionary;
                    if(entries==null)throw new InvalidOperationException("Parallax scatter registry unavailable");
                    foreach(object scatter in entries.Values){if(scatter==null)throw new InvalidOperationException("Invalid Parallax scatter");if((bool)enabled.GetValue(scatter)||(int)index.GetValue(scatter)!=-1)return false;}
                }
                return true;
            }
        }
        internal bool Enforce()
        {
            bool changed=ForceLevelOff();
            if(HasWork){
                object manager=managerInstance.GetValue(null);
                if(manager==null)throw new InvalidOperationException("Cannot safely drain Parallax collisions before initialization");
                // Use the mod's own job completion, native-array disposal and
                // object-pool cleanup. Never destroy terrain or vessel colliders.
                cleanup.Invoke(manager,null);changed=true;
            }
            var planets=bodies.GetValue(null) as IDictionary;
            if(planets==null)throw new InvalidOperationException("Parallax body registry unavailable");
            foreach(object body in planets.Values){
                if(body==null)throw new InvalidOperationException("Invalid Parallax body");
                var entries=scatters.GetValue(body) as IDictionary;
                if(entries==null)throw new InvalidOperationException("Parallax scatter registry unavailable");
                foreach(object scatter in entries.Values){
                    if(scatter==null)throw new InvalidOperationException("Invalid Parallax scatter");
                    changed|=(bool)enabled.GetValue(scatter)||(int)index.GetValue(scatter)!=-1;
                    enabled.SetValue(scatter,false);index.SetValue(scatter,-1);
                }
                changed|=((Array)bodyColliders.GetValue(body))?.Length>0;
                bodyColliders.SetValue(body,emptyScatters);
            }
            managerScatters.SetValue(null,emptyScatters);
            if(Level!=-1||HasWork||!CachedScattersOff)throw new InvalidOperationException("Parallax collisions could not be disabled safely");
            return changed;
        }
    }
}
