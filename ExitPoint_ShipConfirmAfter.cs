using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using System.Data;

using Manh.WMFW.General; //Session - C:\Program Files\Manhattan Associates\ILS\2010\Common\WMW.General.dll
using Manh.WMFW.DataAccess;  //Scale Code to Hit DB - C:\Program Files\Manhattan Associates\ILS\2010\Common\WMW.DataAccess.dll

using Manh.WMW.General; //IWorkFlowStep - C:\Program Files\Manhattan Associates\ILS\2010\Common\WMW.General.dll
using Manh.WMFW.Entities;
using Manh.ILS.NHibernate.Entities; //ShipmentHeaderBE - C:\Program Files\Manhattan Associates\ILS\2010\Common\Manh.WMFW.Entities.dll & C:\Program Files\Manhattan Associates\ILS\2010\Common\EntityBroker.dll
using Manh.ILS.Rating.Interfaces;
using Manh.ILS.IntegrationServices.Entities;
using Newtonsoft.Json;
using System.IO;

using System.Xml.Serialization;
using System.Xml;
using System.Net;
using System.ComponentModel;

namespace BHS.Langham.Toyota.BLL
{
    public class ExitPoint_ShipConfirmAfter : IWorkFlowStep
    {
        decimal runningTotalSurcharge = 0;

        public object ExecuteStep(Manh.WMFW.General.Session session, params object[] parameters)
        {
            ShipmentHeader shipment = null;
            runningTotalSurcharge = 0;

            try
            {
                shipment = (ShipmentHeader)parameters[0];

                //////////////////////////////////////////////
                //begin rating modification 02/17/2020 elegler  -- a lot of the classes used i.e. CarrierRateList, CarrieRateListCriteria... came directly from Portal Code
                //I tried to use the other Rating Web Services from the SDK but neither proved to work, I found this API in Portal code
                //////////////////////////////////////////////

                DataTable ContainersOnShipment;
                decimal runningTotalFreightCharge = 0;
                int totalWeight;

                if (shipment.Warehouse.WarehouseValue == "Toyota MDC-East") 
                {
                    if (shipment.CarrierType != "LTL")
                    {
                        using (DataHelper dataHelper = new DataHelper(session))
                        {
                            IDataParameter[] Parms = new IDataParameter[2];
                            Parms[0] = dataHelper.BuildParameter("@ShipmentID", shipment.ShipmentId);

                            ContainersOnShipment = dataHelper.GetTable(CommandType.StoredProcedure, "BHS_MDC_Move_GetContainersOnShipment", Parms);
                        }

                        totalWeight = CalculateTotalWeight(ContainersOnShipment);

                        foreach (DataRow row in ContainersOnShipment.Rows)
                        {
                            if ((row["Service"].ToString() == "Air" && totalWeight > 100) ||
                                (row["Service"].ToString() == "Ground" && totalWeight > 200) ||
                                row["Service"].ToString() == "International")
                            {
                                WriteDebug("Inside hundredweight if statement");
                                row["TOTAL_WEIGHT"] = totalWeight;
                                runningTotalFreightCharge = SendRequest(session, shipment, row, true, false);
                                break;
                            }
                            else if (row["Service"].ToString() == "FedexGround" && totalWeight > 150)
                            {
                                WriteDebug("Inside Fedex Ground statement");
                                row["TOTAL_WEIGHT"] = totalWeight;
                                runningTotalFreightCharge = SendRequest(session, shipment, row, true, true);
                                break;
                            }
                            else
                            {
                                runningTotalFreightCharge += SendRequest(session, shipment, row, false, false);
                            }
                        }

                        WriteDebug("Before UpdateFreightCharges. Base Freight Charge - " + runningTotalFreightCharge + ". Surcharge - " + runningTotalSurcharge);

                        using (DataHelper dataHelper = new DataHelper(session))
                        {
                            IDataParameter[] Parms = new IDataParameter[3];
                            Parms[0] = dataHelper.BuildParameter("@CaliBaseFreightCharge", runningTotalFreightCharge);
                            Parms[1] = dataHelper.BuildParameter("@ShipmentID", shipment.ShipmentId);
                            Parms[2] = dataHelper.BuildParameter("@Surcharge", runningTotalSurcharge);

                            dataHelper.GetTable(CommandType.StoredProcedure, "BHS_MDC_Move_UpdateFreightCharges", Parms);
                        }
                    }
                    else
                    {
                        WriteDebug("East LTL Shipment. No extra rating necessary.");
                    }
                }

                //Moved CalcAdditionalFreightCharge to after we get the California rate to calculate it based on that charge
                /*
                WriteDebug("Running CalcAdditionalFreightCharge on Shipment ID - " + shipment.ShipmentId);

                using (DataHelper dataHelper = new DataHelper(session))
                {
                    IDataParameter[] Parms = new IDataParameter[2];
                    Parms[0] = dataHelper.BuildParameter("@InternalShipmentNum", shipment.InternalShipmentNum);
                    Parms[1] = dataHelper.BuildParameter("@LaunchNum", shipment.LaunchNum.ToString());

                    dataHelper.GetTable(CommandType.StoredProcedure, "BHS_ExitPoint_CalcAdditionalFreightCharge", Parms);
                }*/

                return null;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.LogException(session, new Exception(ex.Message), "Calculating additional freight charges", session.UserProfile.UserName, string.Format("Internal Shipment Num = {0}", shipment.InternalShipmentNum, shipment));
                return null;
            }
        }

        //this function calculates the total weight based on adding up all of the individual container weights
        //Angie said that this is how total weight should be calculated, rather than using the actual total weight
        public int CalculateTotalWeight(DataTable table)
        {
            int weight = 0;

            foreach (DataRow row in table.Rows)
            {
                weight += Convert.ToInt32(row["WEIGHT"]) * Convert.ToInt32(row["COUNT"]);
            }

            return weight;
        }

        public decimal SendRequest(Manh.WMFW.General.Session session, ShipmentHeader shipment, DataRow containerRow, bool hundredweightFlag, bool fedexGround)
        {
            double weight;
            double volume;

            if (hundredweightFlag)
            {
                if (Double.TryParse(containerRow["TOTAL_WEIGHT"].ToString(), out weight))
                    WriteDebug("Weight as a double - " + weight);
                else
                    WriteDebug("Unable to parse " + containerRow["TOTAL_WEIGHT"].ToString());
            }
            else
            {
                if (Double.TryParse(containerRow["WEIGHT"].ToString(), out weight))
                    WriteDebug("Weight as a double - " + weight);
                else
                    WriteDebug("Unable to parse " + containerRow["WEIGHT"].ToString());
            }

            if (Double.TryParse(containerRow["VOLUME"].ToString(), out volume))
                WriteDebug("Volume as a double - " + volume);
            else
                WriteDebug("Unable to parse " + containerRow["VOLUME"].ToString());

            HttpWebRequest request;

            if (fedexGround)
            {
                request = (HttpWebRequest)WebRequest.Create("http://lang-wms-lab/ILSIntegrationServices/Shipping/carrierRateList");
            }
            else
            {
                //begin sending web request
                request = (HttpWebRequest)WebRequest.Create("http://lang-wms-lab/ILSIntegrationServices/Shipping/carrierRateList");
            }

            request.Method = "POST";
            request.Headers.Add("Session", "ILSSRV:" + shipment.Warehouse.WarehouseValue + ":Toyota");
            request.ContentType = "application/xml";

            WriteDebug("ILSSRV:" + shipment.Warehouse.WarehouseValue + ":Toyota");

            WriteDebug("BEFORE REQUEST BODY FUNCTION. Fedex Ground? " + fedexGround);

            string requestBody = "";

            if (fedexGround)
            {
                requestBody = CreateBodyForPost(shipment, session, weight, volume, true);
            }
            else
            {
                requestBody = CreateBodyForPost(shipment, session, weight, volume, false);
            }

            WriteDebug("After creating the body for the http request. " + requestBody);

            StreamWriter requestStream = null;

            try
            {
                requestStream = new StreamWriter(request.GetRequestStream());
                requestStream.Write(requestBody);
                WriteDebug("after writing the request");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
            finally
            {
                if (requestStream != null)
                {
                    requestStream.Close();
                }
            }

            HttpWebResponse response = null;

            try
            {
                try
                {
                    response = request.GetResponse() as HttpWebResponse;

                }
                catch (System.Net.WebException ex)
                {
                    // all non-200 (OK) responses are thrown as WebExceptions by .NET.
                    if (ex.Response != null)
                        response = ex.Response as HttpWebResponse;

                    else
                        throw;
                }

                StreamReader responseStream = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
                var xml = responseStream.ReadToEnd();
                CarrierRateList responseObject;

                WriteDebug("Before trying to deserialize the response.     " + xml);

                XmlSerializer serializer = new XmlSerializer(typeof(CarrierRateList));
                using (StringReader reader = new StringReader(xml))
                {
                    responseObject = (CarrierRateList)(serializer.Deserialize(reader));
                }

                WriteDebug("After trying to deserialize the response");

                //responseObject now has a list of Carrier Rates in it, if carrier service was specified it will only return one value
                //responseObject.CarrierRate[0].whateverValueYouWant

                WriteDebug(responseObject.ToString());

                WriteDebug("Alleged California Base Freight Charge - " + responseObject.CarrierRate[0].GrossFreightCharge 
                    + ". Number of containers at this rate - " + containerRow["COUNT"].ToString() + ". Hundredweight? - " + hundredweightFlag.ToString());

                decimal priceForTheseContainers = 0;

                if (hundredweightFlag)
                {
                    //grossFreightCharge represents the rate for one of this container
                    priceForTheseContainers = ((Convert.ToDecimal(responseObject.CarrierRate[0].GrossFreightCharge)));
                    runningTotalSurcharge = Convert.ToDecimal(responseObject.CarrierRate[0].FreightSurcharge);
                    WriteDebug("Surcharge for this set of containers - " + (Convert.ToDecimal(responseObject.CarrierRate[0].FreightSurcharge)).ToString());
                }
                else
                {
                    //grossFreightCharge represents the rate for one of this container
                    priceForTheseContainers = (Convert.ToDecimal(responseObject.CarrierRate[0].GrossFreightCharge) * Convert.ToDecimal(containerRow["COUNT"]));
                    runningTotalSurcharge += Convert.ToDecimal(responseObject.CarrierRate[0].FreightSurcharge) * Convert.ToDecimal(containerRow["COUNT"]);
                    WriteDebug("Surcharge for this set of containers - " + (Convert.ToDecimal(responseObject.CarrierRate[0].FreightSurcharge) * Convert.ToDecimal(containerRow["COUNT"])).ToString());
                }

                WriteDebug("Price for this set of containers - " + priceForTheseContainers.ToString());

                return priceForTheseContainers;
            }
            finally
            {
                response.Close();
            }
        }
        

        public string CreateBodyForPost(ShipmentHeader shipment, Manh.WMFW.General.Session session, double weight, double volume, bool fedexGround)
        {
            CarrierRateListCriteria list = new CarrierRateListCriteria();
            //if (fedexGround)
            //{
            //    list.ShipmentId = shipment.ShipmentId;   //don't know why but despite this being set it doesn't get included in the xml, 
            //}                                            //I manually insert this into the xml at the bottom
            list.TotalWeight = weight;
            list.TotalVolume = volume;
            list.Carrier = shipment.Carrier;
            list.CarrierService = shipment.CarrierService;
            list.CarrierGroup = "";
            list.Company = "";
            list.FreightTerms = shipment.FreightTerms;
            list.Customer = shipment.Customer;
            list.ShipTo = shipment.ShipTo;
            list.ShipToAddress = new BHS.Langham.Toyota.BLL.Address();
                list.ShipToAddress.Address1 = shipment.ShipToAddress1;
                list.ShipToAddress.Address2 = "";
                list.ShipToAddress.Address3 = "";
                list.ShipToAddress.AttentionTo = "";
                list.ShipToAddress.City = shipment.ShipToCity;
                list.ShipToAddress.Country = shipment.ShipToCountry;
                list.ShipToAddress.EmailAddress = "";
                list.ShipToAddress.FaxNum = shipment.ShipToFaxNum;
                list.ShipToAddress.Name = shipment.ShipToName;
                list.ShipToAddress.PhoneNum = shipment.ShipToPhoneNum;
                list.ShipToAddress.PostalCode = shipment.ShipToPostalCode;
                list.ShipToAddress.State = shipment.ShipToState;
            
            list.Warehouse = "Toyota MDC-West";
            list.WeightUm = shipment.WeightUm;
            list.VolumeUm = shipment.VolumeUm;

            XmlSerializer serializer = new XmlSerializer(typeof(CarrierRateListCriteria));
            var stringWriter = new StringWriter();
            string stXML = "";
            using (var writer = XmlWriter.Create(stringWriter))
            {
                serializer.Serialize(writer, list);
                stXML = stringWriter.ToString();
            }


            XmlDocument doc = new XmlDocument();
            doc.LoadXml(stXML);

            /*if (fedexGround)
            {
                
                XmlNode shipmentIdNode = doc.CreateNode(XmlNodeType.Element, "ShipmentId", doc.DocumentElement.NamespaceURI);
                shipmentIdNode.InnerText = shipment.ShipmentId;
                doc.DocumentElement.InsertBefore(shipmentIdNode, doc.DocumentElement.FirstChild);
                
            }*/

            XmlNode shipmentIdNode = doc.CreateNode(XmlNodeType.Element, "ShipmentId", doc.DocumentElement.NamespaceURI);

            if (fedexGround)
                shipmentIdNode.InnerText = shipment.ShipmentId;

            doc.DocumentElement.InsertBefore(shipmentIdNode, doc.DocumentElement.FirstChild);

            return doc.OuterXml;
        }

        private void WriteDebug(string text, [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            Debug.WriteLine(string.Format("{0} : {1} : {2} : {3}", this.GetType().FullName, member, line, text));
        }

    }
}
