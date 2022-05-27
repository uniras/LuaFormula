using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unity.VisualScripting;
using UnityEngine;
using XLua;

namespace LuaFormula
{
    /// <summary>
    /// Lua言語版Formulaノード
    /// </summary>
    [UnitTitle("Lua Formula")]
    [UnitShortTitle("Lua Formula")]
    [UnitCategory("LuaFormula")]
    public class LuaFormula : Unit, IMultiInputUnit
    {
        /// <summary>
        /// 実行するLuaコードを入力します。
        /// 
        /// 入力データポートにつなげたノードのデータは、
        /// Lua上ではAにつなげたものはグローバル変数A Bにつなげたものはグローバル変数Bに格納されます。
        /// </summary>
        [Inspectable, InspectorTextArea, UnitHeaderInspectable]
        public string LuaCode { get; private set; }

        /// <summary>
        /// Luaコード実行の結果、エラーや例外が発生しなかった場合はTrueになります。
        /// </summary>
        [DoNotSerialize]
        public ValueOutput Success { get; private set; }

        /// <summary>
        /// Luaコード実行の結果、エラーや例外が発生した場合に例外の文字列が格納されます。
        /// 発生しなかった場合は空文字列になります。
        /// </summary>
        [DoNotSerialize]
        public ValueOutput ErrorMessage { get; private set; }

        /// <summary>
        /// Luaコード内でグローバル変数Rに代入した値が格納されます。
        /// ただし、res変数内にテーブルを代入するとLuaTable型が格納され、
        /// そのままではVisual Scripting内でデータを扱えないので注意してください。
        /// </summary>
        [DoNotSerialize]
        public ValueOutput Result { get; private set; }

        /// <summary>
        /// 入力データポートに接続したデータを次のLuaFormulaノードでも使えるようにするかどうか設定します。
        /// チェックをつけると入力データポート関係の変数A～Iの内容が終了後も保持されます。
        /// 
        /// ※Luaコード内でA～Iの変数の内容を変更した場合はその変更も保持されます。
        /// ※変数を保持していても次のLuaFormulaノードで入力データポートにノードを接続した場合は接続したノードデータに上書きされます。
        /// </summary>
        [Serialize]
        [Inspectable(order = int.MaxValue)]
        [InspectorExpandTooltip]
        public bool cacheArguments { get; set; }


        /// <summary>
        /// 入力データポートの数を指定できます。
        /// 接続したノードのデータはLua環境にA～Iのグローバル変数として格納されます。
        /// 0～9の間で設定できます。
        /// </summary>
        [DoNotSerialize]
        [Inspectable, UnitHeaderInspectable("Inputs")]
        public virtual int inputCount
        {
            get
            {
                return _inputCount;
            }
            set
            {
                _inputCount = Mathf.Clamp(value, minInputCount, maxInputCount);
            }
        }

        [DoNotSerialize]
        public ReadOnlyCollection<ValueInput> multiInputs { get; protected set; }

        [SerializeAs(nameof(inputCount))]
        private int _inputCount = 0;

        [DoNotSerialize]
        protected virtual int maxInputCount => 9;

        [DoNotSerialize]
        protected virtual int minInputCount => 0;



        [DoNotSerialize, PortLabelHidden]
        public ControlInput InputTrigger { get; private set; }

        [DoNotSerialize, PortLabelHidden]
        public ControlOutput OutputTrigger { get; private set; }


        private bool _isSuccess;
        private string _errorMessage;
        private object _resultValue;


        protected override void Definition()
        {
            var mi = new List<ValueInput>();
            multiInputs = mi.AsReadOnly();

            for (var i = 0; i < inputCount; i++)
            {
                mi.Add(ValueInput<object>(((char)('a' + i)).ToString()));
            }

            foreach (var input in multiInputs)
            {
                input.AllowsNull();
            }

            InputTrigger = ControlInput(nameof(InputTrigger), Execute);
            OutputTrigger = ControlOutput(nameof(OutputTrigger));
            Success = ValueOutput<bool>(nameof(Success), _ => _isSuccess);
            ErrorMessage = ValueOutput<string>(nameof(ErrorMessage), _ => _errorMessage);
            Result = ValueOutput<object>(nameof(Result), _ => _resultValue);

            /*
            foreach (var input in multiInputs)
            {
                Requirement(input, Success);
                Requirement(input, ErrorMessage);
                Requirement(input, Result);
            }
            */

            Succession(InputTrigger, OutputTrigger);
        }

        private ControlOutput Execute(Flow flow)
        {
            try
            {
                LuaEnvironment.prepare();

                LuaEnvironment.luaenv.Global.Set<string, object>("R", null);

                for (var i = 0; i < inputCount; i++)
                {
                    try
                    {
                        LuaEnvironment.luaenv.Global.Set<string, object>(((char)('A' + i)).ToString(), flow.GetValue<object>(multiInputs[i]));
                    }
                    catch (MissingValuePortInputException)
                    {
                        //LuaEnvironment.luaenv.Global.Set<string, object>(((char)('A' + i)).ToString(), null);
                    }
                }

                LuaEnvironment.luaenv.DoString(LuaCode);

                if (!cacheArguments) {
                    for (var i = 0; i < maxInputCount; i++)
                    {
                        LuaEnvironment.luaenv.Global.Set<string, object>(((char)('A' + i)).ToString(), null);
                    }
                }
                _isSuccess = true;
                _errorMessage = "";
                _resultValue = LuaEnvironment.luaenv.Global.Get<object>("R");
            }
            catch (Exception e)
            {
#if UNITY_EDITOR
                Debug.LogError(e.ToString());
#endif
                _isSuccess = false;
                _errorMessage = e.ToString();
                _resultValue = null;
            }

            return OutputTrigger;
        }
    }

    
    /// <summary>
    /// xLua用ヘルパー関数群
    /// </summary>
    public static class LuaEnvironment
    {
        public static LuaEnv luaenv = null;

        /// <summary>
        /// Luaの初期化(初期化済みの場合は何もしない)
        /// </summary>
        public static void init()
        {
            if (luaenv == null)
            {
                luaenv = new LuaEnv();
                luaenv.DoString(@"
                    LF = {
                        IsUnityEditor = false,

                        --Log出力用の文字列を生成
                        _LogString = function(msg)
                            local fdata = debug.getinfo(3)
                            local parent = fdata.name
                            local fpath = fdata.source

                            if string.sub(fpath, 1, 1) == '@' then
                                fpath = string.sub(fpath, 2)
                            end

                            if parent == nil then
                                parent = 'Global'
                            end

                            if not LF.IsUseCustomLoader and fpath ~= 'chunk' then
                                fpath = 'Assets/Resources/'..fpath..'.txt'
                            end

                            return tostring(msg)..'\n'..parent..' (at <a href=""'..fpath..'"" line=""'..fdata.currentline..'"">'..fpath..':'..fdata.currentline..'</a>)';
                        end;

                        --Debug.Logラッパー(呼び出し元のLuaコードのファイル名と行位置も出力します)
                        Log = function(msg)
                            if LF.IsUnityEditor then
                                CS.UnityEngine.Debug.Log(LF._LogString(msg))
                            end
                        end;

                        --Debug.LogWarningラッパー(呼び出し元のLuaコードのファイル名と行位置も出力します)
                        LogWarning = function(msg)
                            if LF.IsUnityEditor then
                                CS.UnityEngine.Debug.LogWarning(LF._LogString(msg))
                            end
                        end;

                        --Debug.LogErrorラッパー(呼び出し元のLuaコードのファイル名と行位置も出力します)
                        LogError = function(msg)
                            if LF.IsUnityEditor then
                                CS.UnityEngine.Debug.LogError(LF._LogString(msg))
                            end
                        end;

                        --テーブル以外の変数を文字列に変換
                        _SerializeBaseType = function(key, value)
                            local prestr
                            local convstr
                            if type(key) == 'number' then
                                prestr = ''
                            else
                                prestr = '['..string.format('%q', key)..']='
                            end

                            if type(value) == 'number' then
                                convstr = tostring(value)
                            elseif type(value) == 'string' then
                                convstr = string.format('%q', value)
                            elseif type(value) == 'boolean' then
                                convstr = value and 'true' or 'false'
                            else
                                if type(key) == 'number' then
                                    return '""""'
                                else
                                    return ''
                                end
                            end

                            return prestr..convstr
                        end;

                        --テーブルの内容を列挙して文字列に変換(多次元、循環参照対応)
                        _SerializeTable = function(cycle, key, value)
                            local tablestr = ''
                            local valstring = ''
                            local cskip = true
                            if type(value) == 'table' then
                                if cycle[tostring(value)] then
                                    tablestr = tablestr..LF._SerializeBaseType(key, nil)        
                                else
                                    cycle[tostring(value)] = key
                                    tablestr = tablestr..'{'
                                    for k,v in pairs(value) do
                                        valstring = LF._SerializeTable(cycle, k, v)
                                        if valstring ~= '' then
                                            if cskip then
                                                cskip = false
                                            else
                                                tablestr = tablestr..', '
                                            end
                                            tablestr = tablestr..valstring
                                        end
                                    end
                                    tablestr = tablestr..'}'
                                end
                            else
                                tablestr = tablestr..LF._SerializeBaseType(key, value)
                            end
                            return tablestr;
                        end;

                        --変数を文字列にシリアライズ
                        Serialize = function(value)
                            cycle = {}
                            return LF._SerializeTable(cycle, '', value)
                        end;

                        --シリアライズした文字列を変数にデシリアライズ
                        Deserialize = function(objname, str)
                            assert(load(objname..'='..str))()
                        end;
                    }
                ");

                LuaTable LF = luaenv.Global.Get<LuaTable>("LF");

#if UNITY_EDITOR
                LF.Set("IsUnityEditor", true);
#else
                LF.Set("IsUnityEditor", false);
#endif
            }
        }

        /// <summary>
        /// Luaの初期化・準備(初期化済みの場合はGCを実行)
        /// </summary>
        public static void prepare()
        {
            if (luaenv == null)
            {
                init();
            }
            else
            {
                luaenv.Tick();
            }
        }

        /// <summary>
        /// 文字列をLuaコードとして実行
        /// </summary>
        /// <param name="code">実行するLuaコード</param>
        public static void ExecuteString(string code)
        {
            if (luaenv == null) return;
            luaenv.DoString(code);
        }

        /// <summary>
        /// Luaモジュールを読み込む
        /// </summary>
        /// <param name="modPath">読み込むLuaファイルのパス</param>
        public static void RequireModule(string modPath)
        {
            ExecuteString($"require '{modPath}'");
        }

        /// <summary>
        /// 変数名から簡易シリアライズ
        /// 
        /// ※簡易的なシリアライズ処理のため、複雑なテーブルの場合上手くいかない可能性があります。
        /// 　また、関数や循環参照となるテーブル等は無視されシリアライズされません。
        /// 　ちゃんとしたい場合はjson.luaなどを導入してJSONでやり取りすることをおすすめします。
        /// </summary>
        /// <param name="name">シリアライズする数名</param>
        /// <returns>シリアライズされた文字列</returns>
        public static string Serialize(string name)
        {
            ExecuteString($"LF_result = LF.Serialize({name})");
            string ret = luaenv.Global.Get<string>("LF_result");
            return ret;
        }

        /// <summary>
        /// LuaTableから簡易シリアライズ
        /// </summary>
        /// <param name="table">シリアライズするLuaTableオブジェクト</param>
        /// <returns>シリアライズされた文字列</returns>
        public static string Serialize(LuaTable table)
        {
            luaenv.Global.Set<string, LuaTable>("LF_intable", table);
            ExecuteString("LF_result = LF.Serialize(LF_intable)");
            string ret = luaenv.Global.Get<string>("LF_result");
            return ret;
        }

        /// <summary>
        /// 簡易シリアライズ文字列を指定した文字列の変数にデシリアライズ
        /// 
        /// ※単純にLuaコードとして実行することでデシリアライズしているため、
        /// 　不正な文字列を渡すと思わぬ動作を引き起こすことがあります。
        /// 　ちゃんとしたい場合はjson.luaなどを導入してJSONでやり取りすることをおすすめします。
        /// </summary>
        /// <param name="name">デシリアライズされたテーブルを代入する変数名</param>
        /// <param name="data">シリアライズされている文字列</param>
        public static void DeSerialize(string name, string data)
        {
            luaenv.Global.Set<string, string>("LF_instring", data);
            ExecuteString($"LF.Deserialize('{name}', LF_instring)");
        }

        /// <summary>
        /// Luaグローバル変数に値をセットする
        /// </summary>
        /// <param name="key">値をセットする変数名</param>
        /// <param name="dat">セットする値</param>
        public static void GlobalSet(string key, object dat)
        {
            if (luaenv == null) return;
            luaenv.Global.Set(key, dat);
        }

        /// <summary>
        /// Luaグローバル変数の値を取得
        /// 
        /// ※この関数にテーブルが設定されている変数を指定するとLuaTable型の値が戻るためそのままではC#では扱えません。
        /// 　リストやハッシュになっている値を取得するにはGlobalGetListやGlobalGetDictionary関数を使います。
        /// 　
        /// </summary>
        /// <param name="key">値を取得する変数名</param>
        /// <returns>指定した変数に設定されていた値、テーブルだった場合はLuaTableオブジェクト</returns>
        public static object GlobalGet(string key)
        {
            if (luaenv == null) return null;
            return luaenv.Global.Get<object>(key);
        }

        /// <summary>
        /// Luaグローバル変数の内容をリストとして取得
        /// 
        /// ※2次元以上のリストには対応していません。リスト内のリストやハッシュはLuaTableオブジェクトになります。
        /// </summary>
        /// <param name="key">Listを取得する変数名</param>
        /// <returns>指定した変数に設定されていたリスト</returns>
        public static List<object> GlobalGetList(string key)
        {
            if (luaenv == null) return null;
            return luaenv.Global.Get<List<object>>(key);
        }

        /// <summary>
        /// Luaグローバル変数の内容をハッシュとして取得
        /// 
        /// ※2次元以上のハッシュには対応していません。ハッシュ内のリストやハッシュはLuaTableオブジェクトになります。
        /// </summary>
        /// <param name="key">ハッシュを取得する変数名</param>
        /// <returns>指定した変数に設定されていたハッシュ</returns>
        public static Dictionary<string, object> GlobalGetDictionary(string key)
        {
            if (luaenv == null) return null;
            return luaenv.Global.Get<Dictionary<string, object>>(key);
        }

        /// <summary>
        /// LuaTableに値を設定
        /// </summary>
        /// <param name="table">値をセットするLuaTableオブジェクト</param>
        /// <param name="key">値をセットするLuaTableオブジェクト内の変数名</param>
        /// <param name="dat">セットする値</param>
        public static void TableSet(LuaTable table, string key, object dat)
        {
            table.Set(key, dat);
        }

        /// <summary>
        /// LuaTableの値を取得
        ///
        /// ※この関数にテーブルが設定されているLuaTableを渡すとLuaTable型の値が戻るためそのままではC#では扱えません。
        /// 　リストやハッシュになっている値を取得するにはTableGetListやTableGetDictionary関数を使います。
        /// 　
        /// </summary>
        /// <param name="table">値を取得するLuaTableオブジェクト</param>
        /// <param name="key">値を取得する変数名</param>
        /// <returns>指定した変数に設定されていた値、テーブルだった場合はLuaTableオブジェクト</returns>
        public static object TableGet(LuaTable table, string key)
        {
            return table.Get<object>(key);
        }

        /// <summary>
        /// LuaTableのリストを取得
        /// 
        /// ※2次元以上のリストには対応していません。リスト内のリストやハッシュはLuaTableオブジェクトになります。
        /// </summary>
        /// <param name="table">値をセットするLuaTableオブジェクト</param>
        /// <param name="key">値をセットするLuaTableオブジェクト内の変数名</param>
        /// <returns>指定した変数に設定されていたリスト</returns>
        public static List<object> TableGetList(LuaTable table, string key)
        {
            return table.Get<List<object>>(key);
        }

        /// <summary>
        /// LuaTableのハッシュを取得
        /// 
        /// ※2次元以上のハッシュには対応していません。ハッシュ内のリストやハッシュはLuaTableオブジェクトになります。
        /// </summary>
        /// <param name="table">値をセットするLuaTableオブジェクト</param>
        /// <param name="key">値をセットするLuaTableオブジェクト内の変数名</param>
        /// <returns>指定した変数に設定されていたハッシュ</returns>
        public static Dictionary<string, object> TableGetDictionary(LuaTable table, string key)
        {
            return table.Get<Dictionary<string, object>>(key);
        }
    }
}