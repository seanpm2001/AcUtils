﻿/* Copyright (C) 2017 Verizon. All Rights Reserved.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at
    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License. */

// Required references: AcUtils.dll, Microsoft.CSharp, Microsoft.Office.Interop.Excel, System, System.configuration, System.Windows.Forms, System.Xml.Linq
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using Excel = Microsoft.Office.Interop.Excel;
using AcUtils;

namespace LatestTransactions
{
    class Program
    {
        #region class variables
        private static List<string> _depots; // names of all depots in repository
        private static List<XElement> _transactions; // latest transaction in each depot
        private static string _fileName; // from LastestTransactions.exe.config
        private static string _fileLocation; // .. 
        private static readonly object _locker = new object();  // token for lock keyword scope
        #endregion

        [STAThread]
        static int Main(string[] args)
        {
            // general program initialization
            if (!init()) return 1;

            _depots = AcQuery.getDepotNameListAsync().Result;
            if (_depots == null) return 1;

            _transactions = new List<XElement>(_depots.Count);
            if (initTransListAsync().Result == false) return 1;

            return report() ? 0 : 1;
        }

        // Initialize our transaction list with the latest transactions for all depots in the repository.
        // Returns true if initialization succeeded, otherwise false.
        private static async Task<bool> initTransListAsync()
        {
            List<Task<XElement>> tasks = new List<Task<XElement>>(_depots.Count);
            foreach (string depot in _depots)
                tasks.Add(initLastTransAsync(depot));

            XElement[] arr = await Task.WhenAll(tasks); // finish running all in parallel
            return (arr != null && arr.All(n => n != null)); // true if all succeeded
        }

        // Run the hist command for depot and add the results to our transactions list. Returns the 
        // latest transaction in depot on success, otherwise null. AcUtilsException caught and logged 
        // in %LOCALAPPDATA%\AcTools\Logs\LatestTransactions-YYYY-MM-DD.log on hist command failure.
        private async static Task<XElement> initLastTransAsync(string depot)
        {
            XElement trans = null; // assume failure
            try
            {
                string cmd = String.Format(@"hist -p ""{0}"" -t now -fx", depot);
                AcResult result = await AcCommand.runAsync(cmd);
                if (result != null && result.RetVal == 0)
                {
                    XElement xml = XElement.Parse(result.CmdResult);
                    XElement temp = xml.Element("transaction");
                    temp.AddAnnotation(depot); // add depot since it's not in the XML
                    lock (_locker) { _transactions.Add(temp); }
                    trans = temp;
                }
            }

            catch (AcUtilsException exc)
            {
                string msg = String.Format("AcUtilsException caught and logged in Program.initLastTransAsync{0}{1}",
                    Environment.NewLine, exc.Message);
                AcDebug.Log(msg);
            }

            return trans;
        }

        // Generate an Excel worksheet with the results in reverse chronological order so the most 
        // recent activity in the repository is shown on top. Returns true if the operation completed 
        // successfully, false otherwise. COMException caught and logged in 
        // %LOCALAPPDATA%\AcTools\Logs\LatestTransactions-YYYY-MM-DD.log on worksheet creation failure.
        private static bool report()
        {
            Excel.Application excel = new Excel.Application();
            if (excel == null)
            {
                MessageBox.Show("Excel installation not found.", "LatestTransactions", 
                    MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return false;
            }

            bool ret = true; // assume success
            excel.DisplayAlerts = false; // don't display the SaveAs dialog box
            Excel.Workbook wbook = excel.Workbooks.Add();
            Excel.Worksheet wsheet = (Excel.Worksheet)wbook.Worksheets.get_Item(1);
            Excel.Range rheader = wsheet.get_Range("A1", "F1");
            rheader.Font.Bold = true;
            rheader.HorizontalAlignment = Excel.XlHAlign.xlHAlignCenter;
            Excel.Range rdate = wsheet.get_Range("B:B");
            rdate.NumberFormat = "mmm d, yyyy h:mm:ss am/pm";
            Excel.Range rcomment = wsheet.get_Range("F:F");
            rcomment.WrapText = true;

            wsheet.Cells[1, "A"] = "Depot";
            wsheet.Columns["A"].ColumnWidth = 20;
            wsheet.Cells[1, "B"] = "Time";
            wsheet.Columns["B"].ColumnWidth = 25;
            wsheet.Cells[1, "C"] = "Action";
            wsheet.Columns["C"].ColumnWidth = 15;
            wsheet.Cells[1, "D"] = "User";
            wsheet.Columns["D"].ColumnWidth = 15;
            wsheet.Cells[1, "E"] = "Trans_ID";
            wsheet.Columns["E"].ColumnWidth = 10;
            wsheet.Cells[1, "F"] = "Comment";
            wsheet.Columns["F"].ColumnWidth = 50;

            int row = 2;
            foreach (XElement trans in _transactions.OrderByDescending(n => n.acxTime("time")))
            {
                string depot = trans.Annotation<string>();
                int id = (int)trans.Attribute("id");
                string action = (string)trans.Attribute("type");
                DateTime? time = trans.acxTime("time");
                string user = (string)trans.Attribute("user");
                string comment = trans.acxComment();

                wsheet.Cells[row, "A"] = depot;
                wsheet.Cells[row, "B"] = time;
                wsheet.Cells[row, "C"] = action;
                wsheet.Cells[row, "D"] = user;
                wsheet.Cells[row, "E"] = id;
                wsheet.Cells[row, "F"] = comment;
                row++;
            }

            string file = String.Empty;
            try
            {
                file = Path.Combine(_fileLocation, _fileName);
                wbook.SaveAs(Filename: file, ReadOnlyRecommended: true);
                wbook.Close();
            }

            catch (COMException exc)
            {
                AcDebug.Log(exc.Message);
                MessageBox.Show(exc.Message);
                ret = false;
            }

            finally { excel.Quit(); }

            if (ret)
            {
                string msg = String.Format("Latest transactions saved to {0}", file);
                MessageBox.Show(msg, "LatestTransactions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return ret;
        }

        // General program initialization routines. Returns true if initialization was successful, false otherwise.
        private static bool init()
        {
            // initialize our logging support so we can log errors
            if (!AcDebug.initAcLogging())
            {
                Console.WriteLine("Logging support initialization failed.");
                return false;
            }

            // save an unhandled exception in log file before program terminates
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(AcDebug.unhandledException);

            // ensure we're logged into AccuRev
            Task<string> prncpl = AcQuery.getPrincipalAsync();
            if (String.IsNullOrEmpty(prncpl.Result))
            {
                string msg = String.Format("Not logged into AccuRev.{0}Please login and try again.",
                    Environment.NewLine);
                AcDebug.Log(msg);
                return false;
            }

            // initialize our class variables from LatestTransactions.exe.config
            if (!initAppConfigData()) return false;

            return true;
        }

        // Initialize our class variables from LatestTransactions.exe.config. Returns true if class variables 
        // initialized successfully, false otherwise. ConfigurationErrorsException caught and logged in 
        // %LOCALAPPDATA%\AcTools\Logs\LatestTransactions-YYYY-MM-DD.log on initialization failure.
        private static bool initAppConfigData()
        {
            bool ret = true; // assume success
            try
            {
                _fileName = AcQuery.getAppConfigSetting<string>("FileName").Trim();
                _fileLocation = AcQuery.getAppConfigSetting<string>("FileLocation").Trim();
            }

            catch (ConfigurationErrorsException exc)
            {
                Process currentProcess = Process.GetCurrentProcess();
                ProcessModule pm = currentProcess.MainModule;
                string exeFile = pm.ModuleName;
                string msg = String.Format("Invalid data in {1}.config{0}{2}",
                    Environment.NewLine, exeFile, exc.Message);
                AcDebug.Log(msg);
                ret = false;
            }

            return ret;
        }
    }
}
