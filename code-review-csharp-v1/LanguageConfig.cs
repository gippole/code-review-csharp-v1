using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.IO;
using System.Xml;

namespace code_review_csharp_v1;

public record LanguageOption(
    string Id,
    string DisplayName,
    string[] Extensions,
    string MarkdownCodeFence,
    string FileFilter,
    string HighlightingName,
    string SampleCode
);

public static class LanguageConfig
{
    public static readonly IReadOnlyList<LanguageOption> SupportedLanguages = new List<LanguageOption>
    {
        new(
            Id: "csharp",
            DisplayName: "C#",
            Extensions: [".cs"],
            MarkdownCodeFence: "csharp",
            FileFilter: "C# ファイル (*.cs)|*.cs",
            HighlightingName: "C#",
            SampleCode: """
                using System;
                using System.IO;

                public class OrderProcessor
                {
                    // レビューしたい C# コードをここに貼り付けるか、
                    // 上部の「📂 開く」またはファイルをドラッグ＆ドロップしてください。
                    public void ProcessOrder(string customerId, string rawPassword, decimal amount)
                    {
                        // SQL インジェクションのリスク例
                        string sql = "SELECT * FROM Orders WHERE CustomerId = '" + customerId + "'";
                        Console.WriteLine($"SQL: {sql}");

                        // パスワードの平文ログ出力とリソース解放漏れ例
                        var fs = new FileStream("audit.log", FileMode.Append);
                        var writer = new StreamWriter(fs);
                        writer.WriteLine($"Customer {customerId} with pass {rawPassword} charged {amount:C}");
                        writer.Flush();
                        // ※ fs, writer が Dispose されていません
                    }
                }
                """
        ),
        new(
            Id: "c",
            DisplayName: "C",
            Extensions: [".c", ".h"],
            MarkdownCodeFence: "c",
            FileFilter: "C ファイル (*.c;*.h)|*.c;*.h",
            HighlightingName: "C++",
            SampleCode: """
                #include <stdio.h>
                #include <stdlib.h>
                #include <string.h>

                void process_user(const char *input, const char *password) {
                    // バッファオーバーフローの脆弱性例
                    char buffer[64];
                    strcpy(buffer, input);

                    // 平文パスワードのログ出力とメモリリーク例
                    char *log_msg = (char *)malloc(256);
                    if (log_msg != NULL) {
                        sprintf(log_msg, "User input: %s, Pass: %s\n", buffer, password);
                        printf("%s", log_msg);
                        // ※ log_msg が free() されていません
                    }
                }
                """
        ),
        new(
            Id: "cpp",
            DisplayName: "C++",
            Extensions: [".cpp", ".cc", ".cxx", ".hpp", ".hxx", ".h"],
            MarkdownCodeFence: "cpp",
            FileFilter: "C++ ファイル (*.cpp;*.cc;*.cxx;*.hpp;*.hxx;*.h)|*.cpp;*.cc;*.cxx;*.hpp;*.hxx;*.h",
            HighlightingName: "C++",
            SampleCode: """
                #include <iostream>
                #include <fstream>
                #include <string>
                #include <vector>
                #include <stdexcept>

                class UserManager {
                public:
                    void updateUser(const std::string& userId, const std::string& password) {
                        // SQL インジェクションのリスク例
                        std::string query = "UPDATE Users SET password = '" + password + "' WHERE id = '" + userId + "'";
                        std::cout << "Executing: " << query << std::endl;

                        // メモリ解放漏れ / 例外安全性の問題例
                        int* tempBuffer = new int[1024];
                        if (password.empty()) {
                            throw std::invalid_argument("Password cannot be empty"); // tempBuffer がリーク
                        }
                        delete[] tempBuffer;
                    }
                };
                """
        ),
        new(
            Id: "python",
            DisplayName: "Python",
            Extensions: [".py", ".pyw"],
            MarkdownCodeFence: "python",
            FileFilter: "Python ファイル (*.py;*.pyw)|*.py;*.pyw",
            HighlightingName: "Python",
            SampleCode: """
                import sqlite3
                import os

                def authenticate_user(username: str, password: str):
                    # SQL インジェクションの脆弱性例
                    conn = sqlite3.connect("users.db")
                    cursor = conn.cursor()
                    query = f"SELECT * FROM accounts WHERE username = '{username}' AND password = '{password}'"
                    cursor.execute(query)
                    user = cursor.fetchone()

                    # 安全でないファイル操作とリソースリーク例
                    log_file = open("auth_audit.log", "a")
                    log_file.write(f"Login attempt for user: {username}, pass: {password}\n")
                    # ※ log_file が close() されておらず、パスワードが平文保存されている

                    return user is not None
                """
        ),
        new(
            Id: "dart",
            DisplayName: "Dart",
            Extensions: [".dart"],
            MarkdownCodeFence: "dart",
            FileFilter: "Dart ファイル (*.dart)|*.dart",
            HighlightingName: "Dart",
            SampleCode: """
                import 'dart:io';

                class AuthService {
                  // レビューしたい Dart コードをここに貼り付けてください
                  Future<void> loginUser(String username, String password) async {
                    // パスワードの平文ログ出力例
                    print('User $username attempting login with password: $password');

                    // 未処理の例外リスクとリソースリーク例
                    final file = File('audit.log');
                    final sink = file.openWrite(mode: FileMode.append);
                    sink.writeln('Auth request: $username');
                    // ※ await sink.flush() / sink.close() が実行されていません

                    // 安全でないコマンド実行例
                    final result = await Process.run('sh', ['-c', 'echo user: $username']);
                    print(result.stdout);
                  }
                }
                """
        )
    };

    private static bool _customHighlightingsRegistered;

    public static void RegisterCustomHighlightings()
    {
        if (_customHighlightingsRegistered) return;
        _customHighlightingsRegistered = true;

        RegisterSyntaxHighlighting("Python", [".py", ".pyw"], PythonXshdXml);
        RegisterSyntaxHighlighting("Dart", [".dart"], DartXshdXml);
    }

    private static void RegisterSyntaxHighlighting(string name, string[] extensions, string xshdXml)
    {
        try
        {
            using var stringReader = new StringReader(xshdXml);
            using var xmlReader = XmlReader.Create(stringReader);
            var xshd = HighlightingLoader.LoadXshd(xmlReader);
            var definition = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
        }
        catch
        {
            // Ignore if already registered or failure
        }
    }

    public static LanguageOption? FindByFilePath(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        return SupportedLanguages.FirstOrDefault(lang =>
            lang.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)));
    }

    public static string BuildFullOpenFileDialogFilter()
    {
        var allExts = string.Join(";", SupportedLanguages.SelectMany(l => l.Extensions.Select(e => $"*{e}")));
        var parts = new List<string>
        {
            $"すべての対応ソースファイル ({allExts})|{allExts}"
        };

        foreach (var lang in SupportedLanguages)
        {
            parts.Add(lang.FileFilter);
        }

        parts.Add("すべてのファイル (*.*)|*.*");
        return string.Join("|", parts);
    }

    private const string PythonXshdXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="Python" extensions=".py;.pyw" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6A9955" />
          <Color name="String" foreground="#CE9178" />
          <Color name="Keywords" foreground="#569CD6" fontWeight="bold" />
          <Color name="Builtin" foreground="#4EC9B0" />
          <Color name="Number" foreground="#B5CEA8" />
          <Color name="Decorator" foreground="#DCDCAA" />

          <RuleSet>
            <Span color="Comment" begin="#" />

            <Span color="String" multiline="true">
              <Begin>\"\"\"</Begin>
              <End>\"\"\"</End>
            </Span>
            <Span color="String" multiline="true">
              <Begin>'''</Begin>
              <End>'''</End>
            </Span>

            <Span color="String">
              <Begin>"</Begin>
              <End>"</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>
            <Span color="String">
              <Begin>'</Begin>
              <End>'</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>

            <Span color="Decorator">
              <Begin>@</Begin>
              <End>\b</End>
            </Span>

            <Keywords color="Keywords">
              <Word>and</Word>
              <Word>as</Word>
              <Word>assert</Word>
              <Word>async</Word>
              <Word>await</Word>
              <Word>break</Word>
              <Word>class</Word>
              <Word>continue</Word>
              <Word>def</Word>
              <Word>del</Word>
              <Word>elif</Word>
              <Word>else</Word>
              <Word>except</Word>
              <Word>finally</Word>
              <Word>for</Word>
              <Word>from</Word>
              <Word>global</Word>
              <Word>if</Word>
              <Word>import</Word>
              <Word>in</Word>
              <Word>is</Word>
              <Word>lambda</Word>
              <Word>nonlocal</Word>
              <Word>not</Word>
              <Word>or</Word>
              <Word>pass</Word>
              <Word>raise</Word>
              <Word>return</Word>
              <Word>try</Word>
              <Word>while</Word>
              <Word>with</Word>
              <Word>yield</Word>
              <Word>match</Word>
              <Word>case</Word>
            </Keywords>

            <Keywords color="Builtin">
              <Word>True</Word>
              <Word>False</Word>
              <Word>None</Word>
              <Word>self</Word>
              <Word>cls</Word>
              <Word>int</Word>
              <Word>str</Word>
              <Word>float</Word>
              <Word>bool</Word>
              <Word>list</Word>
              <Word>dict</Word>
              <Word>set</Word>
              <Word>tuple</Word>
              <Word>print</Word>
              <Word>len</Word>
              <Word>range</Word>
              <Word>type</Word>
              <Word>isinstance</Word>
              <Word>open</Word>
              <Word>super</Word>
            </Keywords>

            <Rule color="Number">
              \b0[xX][0-9a-fA-F]+\b|\b0[bB][01]+\b|\b\d+(\.[0-9]+)?([eE][+-]?[0-9]+)?\b
            </Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string DartXshdXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="Dart" extensions=".dart" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6A9955" />
          <Color name="String" foreground="#CE9178" />
          <Color name="Keywords" foreground="#569CD6" fontWeight="bold" />
          <Color name="Builtin" foreground="#4EC9B0" />
          <Color name="Number" foreground="#B5CEA8" />
          <Color name="Annotation" foreground="#DCDCAA" />

          <RuleSet>
            <Span color="Comment" begin="//" />
            <Span color="Comment" multiline="true" begin="/\*" end="\*/" />

            <Span color="String" multiline="true">
              <Begin>'''</Begin>
              <End>'''</End>
            </Span>
            <Span color="String" multiline="true">
              <Begin>\"\"\"</Begin>
              <End>\"\"\"</End>
            </Span>

            <Span color="String">
              <Begin>"</Begin>
              <End>"</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>
            <Span color="String">
              <Begin>'</Begin>
              <End>'</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>

            <Span color="Annotation">
              <Begin>@</Begin>
              <End>\b</End>
            </Span>

            <Keywords color="Keywords">
              <Word>abstract</Word>
              <Word>as</Word>
              <Word>assert</Word>
              <Word>async</Word>
              <Word>await</Word>
              <Word>break</Word>
              <Word>case</Word>
              <Word>catch</Word>
              <Word>class</Word>
              <Word>const</Word>
              <Word>continue</Word>
              <Word>covariant</Word>
              <Word>default</Word>
              <Word>deferred</Word>
              <Word>do</Word>
              <Word>dynamic</Word>
              <Word>else</Word>
              <Word>enum</Word>
              <Word>export</Word>
              <Word>extends</Word>
              <Word>extension</Word>
              <Word>external</Word>
              <Word>factory</Word>
              <Word>false</Word>
              <Word>final</Word>
              <Word>finally</Word>
              <Word>for</Word>
              <Word>Function</Word>
              <Word>get</Word>
              <Word>hide</Word>
              <Word>if</Word>
              <Word>implements</Word>
              <Word>import</Word>
              <Word>in</Word>
              <Word>interface</Word>
              <Word>is</Word>
              <Word>late</Word>
              <Word>library</Word>
              <Word>mixin</Word>
              <Word>new</Word>
              <Word>null</Word>
              <Word>on</Word>
              <Word>operator</Word>
              <Word>part</Word>
              <Word>required</Word>
              <Word>rethrow</Word>
              <Word>return</Word>
              <Word>set</Word>
              <Word>show</Word>
              <Word>static</Word>
              <Word>super</Word>
              <Word>switch</Word>
              <Word>sync</Word>
              <Word>this</Word>
              <Word>throw</Word>
              <Word>true</Word>
              <Word>try</Word>
              <Word>typedef</Word>
              <Word>var</Word>
              <Word>void</Word>
              <Word>while</Word>
              <Word>with</Word>
              <Word>yield</Word>
            </Keywords>

            <Keywords color="Builtin">
              <Word>int</Word>
              <Word>double</Word>
              <Word>num</Word>
              <Word>String</Word>
              <Word>bool</Word>
              <Word>List</Word>
              <Word>Map</Word>
              <Word>Set</Word>
              <Word>Future</Word>
              <Word>Stream</Word>
              <Word>Object</Word>
              <Word>Type</Word>
              <Word>print</Word>
            </Keywords>

            <Rule color="Number">
              \b0[xX][0-9a-fA-F]+\b|\b\d+(\.[0-9]+)?([eE][+-]?[0-9]+)?\b
            </Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}