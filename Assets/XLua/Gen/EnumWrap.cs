#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using XLua;
using System.Collections.Generic;


namespace XLua.CSObjectWrap
{
    using Utils = XLua.Utils;
    
    public class TMProTextAlignmentOptionsWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(TMPro.TextAlignmentOptions), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(TMPro.TextAlignmentOptions), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(TMPro.TextAlignmentOptions), L, null, 38, 0, 0);

            Utils.RegisterEnumType(L, typeof(TMPro.TextAlignmentOptions));

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(TMPro.TextAlignmentOptions), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushTMProTextAlignmentOptions(L, (TMPro.TextAlignmentOptions)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

                try
				{
                    translator.TranslateToEnumToTop(L, typeof(TMPro.TextAlignmentOptions), 1);
				}
				catch (System.Exception e)
				{
					return LuaAPI.luaL_error(L, "cast to " + typeof(TMPro.TextAlignmentOptions) + " exception:" + e);
				}

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for TMPro.TextAlignmentOptions! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class TMProFontStylesWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(TMPro.FontStyles), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(TMPro.FontStyles), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(TMPro.FontStyles), L, null, 12, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Normal", TMPro.FontStyles.Normal);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Bold", TMPro.FontStyles.Bold);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Italic", TMPro.FontStyles.Italic);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Underline", TMPro.FontStyles.Underline);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "LowerCase", TMPro.FontStyles.LowerCase);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "UpperCase", TMPro.FontStyles.UpperCase);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "SmallCaps", TMPro.FontStyles.SmallCaps);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Strikethrough", TMPro.FontStyles.Strikethrough);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Superscript", TMPro.FontStyles.Superscript);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Subscript", TMPro.FontStyles.Subscript);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Highlight", TMPro.FontStyles.Highlight);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(TMPro.FontStyles), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushTMProFontStyles(L, (TMPro.FontStyles)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Normal"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Normal);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Bold"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Bold);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Italic"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Italic);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Underline"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Underline);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "LowerCase"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.LowerCase);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "UpperCase"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.UpperCase);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "SmallCaps"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.SmallCaps);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Strikethrough"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Strikethrough);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Superscript"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Superscript);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Subscript"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Subscript);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Highlight"))
                {
                    translator.PushTMProFontStyles(L, TMPro.FontStyles.Highlight);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for TMPro.FontStyles!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for TMPro.FontStyles! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class TutorialTestEnumWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(Tutorial.TestEnum), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(Tutorial.TestEnum), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(Tutorial.TestEnum), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "E1", Tutorial.TestEnum.E1);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "E2", Tutorial.TestEnum.E2);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(Tutorial.TestEnum), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushTutorialTestEnum(L, (Tutorial.TestEnum)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "E1"))
                {
                    translator.PushTutorialTestEnum(L, Tutorial.TestEnum.E1);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "E2"))
                {
                    translator.PushTutorialTestEnum(L, Tutorial.TestEnum.E2);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for Tutorial.TestEnum!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for Tutorial.TestEnum! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class XLuaTestMyEnumWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(XLuaTest.MyEnum), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(XLuaTest.MyEnum), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(XLuaTest.MyEnum), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "E1", XLuaTest.MyEnum.E1);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "E2", XLuaTest.MyEnum.E2);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(XLuaTest.MyEnum), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushXLuaTestMyEnum(L, (XLuaTest.MyEnum)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "E1"))
                {
                    translator.PushXLuaTestMyEnum(L, XLuaTest.MyEnum.E1);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "E2"))
                {
                    translator.PushXLuaTestMyEnum(L, XLuaTest.MyEnum.E2);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for XLuaTest.MyEnum!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for XLuaTest.MyEnum! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class TutorialDerivedClassTestEnumInnerWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(Tutorial.DerivedClass.TestEnumInner), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(Tutorial.DerivedClass.TestEnumInner), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(Tutorial.DerivedClass.TestEnumInner), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "E3", Tutorial.DerivedClass.TestEnumInner.E3);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "E4", Tutorial.DerivedClass.TestEnumInner.E4);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(Tutorial.DerivedClass.TestEnumInner), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushTutorialDerivedClassTestEnumInner(L, (Tutorial.DerivedClass.TestEnumInner)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "E3"))
                {
                    translator.PushTutorialDerivedClassTestEnumInner(L, Tutorial.DerivedClass.TestEnumInner.E3);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "E4"))
                {
                    translator.PushTutorialDerivedClassTestEnumInner(L, Tutorial.DerivedClass.TestEnumInner.E4);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for Tutorial.DerivedClass.TestEnumInner!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for Tutorial.DerivedClass.TestEnumInner! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
}