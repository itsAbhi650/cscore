using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CSCore.Ffmpeg.Interops
{
    internal class UTF8Marshaler : ICustomMarshaler
    {
        private static readonly UTF8Marshaler Instance = new UTF8Marshaler();

        public virtual object MarshalNativeToManaged(IntPtr pNativeData)
        {
            return FromNative(Encoding.UTF8, pNativeData);
        }

        public virtual IntPtr MarshalManagedToNative(object managedObj)
        {
            if (managedObj == null)
                return IntPtr.Zero;

            string str = managedObj as string;
            if (str == null)
                throw new MarshalDirectiveException("UTF8Marshaler must be used on a string.");

            return FromManaged(Encoding.UTF8, str);
        }

        public virtual void CleanUpNativeData(IntPtr pNativeData)
        {
            if (pNativeData != IntPtr.Zero)
                Marshal.FreeHGlobal(pNativeData);
        }

        public void CleanUpManagedData(object managedObj) { }

        public int GetNativeDataSize() { return -1; }

        public static ICustomMarshaler GetInstance(string cookie) { return Instance; }

        public static unsafe string FromNative(Encoding encoding, IntPtr pNativeData)
        {
            return FromNative(encoding, (byte*)pNativeData);
        }

        public static unsafe string FromNative(Encoding encoding, byte* pNativeData)
        {
            if (pNativeData == null)
                return null;

            byte* walk = pNativeData;
            while (*walk != 0) walk++;

            if (walk == pNativeData)
                return string.Empty;

            return new string((sbyte*)pNativeData, 0, (int)(walk - pNativeData), encoding);
        }

        public static unsafe IntPtr FromManaged(Encoding encoding, string str)
        {
            if (str == null)
                return IntPtr.Zero;

            byte[] bytes = encoding.GetBytes(str);
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }
    }
}
