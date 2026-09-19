namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    
    //
    
    
    
    internal static class Cm
    {
        
        public static T Get<T>() where T : UnityEngine.Object
        {
            var v = ComponentManager<T>.Value;                 
            return (UnityEngine.Object)v != null ? v : null;   
        }

        
        
        public static bool TryGet<T>(out T value) where T : UnityEngine.Object
        {
            value = Get<T>();
            return value != null;
        }
    }
}
