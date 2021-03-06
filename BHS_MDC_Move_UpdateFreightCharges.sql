USE [ILS_Toyota]
GO
/****** Object:  StoredProcedure [dbo].[BHS_MDC_Move_UpdateFreightCharges]    Script Date: 8/13/2021 8:09:36 AM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO


ALTER PROCEDURE [dbo].[BHS_MDC_Move_UpdateFreightCharges](
	@CaliBaseFreightCharge decimal(10,3),
	@ShipmentID nvarchar(25),
	@Surcharge decimal(10,3)
)
AS

delete BHS_WarehouseRating where SHIPMENT_ID = @ShipmentID

--declare @PercentOfFreightToBill    decimal(10,5)
declare @CALI_TOTAL_FREIGHT_CHARGE decimal(10,3)
declare @INDY_BASE_FREIGHT_CHARGE decimal(10,3)
declare @INDY_TOTAL_FREIGHT_CHARGE decimal(10,3)

declare @FedExFuelSurcharge decimal(28,5)
set @FedExFuelSurcharge = isnull((
	select CONVERT(decimal(28,5), system_value)
	from SYSTEM_CONFIG_DETAIL
	where SYS_KEY = 'FedExFuelSurcharge'		--both of these variables are from [BHS_InterfaceUpload_PopulateIntermediateTables]
	and RECORD_TYPE = 'Gen Rating'
	and ISNUMERIC(SYSTEM_VALUE) = 1
), 1)

declare @FedExFuelSurchargeExpress decimal(28,5)
set @FedExFuelSurchargeExpress = ISNULL((
	select CONVERT(decimal(28,5), system_value)
	from SYSTEM_CONFIG_DETAIL
	WHERE SYS_KEY = 'FedExFuelSurchargeExpress'
	and RECORD_TYPE = 'Gen Rating'
	and ISNUMERIC(SYSTEM_VALUE) = 1
), 1)

--calculate total freight charge, total freight charge includes fuel surcharge and discount if the carrier service is AIR
--select @PercentOfFreightToBill = CONVERT(DOUBLE PRECISION, c.USER_DEF7/100)
--from CARRIER c 
--join SHIPMENT_HEADER sh on sh.carrier = c.carrier and sh.carrier_service = c.service
--where sh.shipment_id = @ShipmentID

select @CALI_TOTAL_FREIGHT_CHARGE = 
	CAST(((@CaliBaseFreightCharge * PercentOfFreightToBill) * --air discount needs to be calculated before fuel surcharge
		(FUEL_SURCHARGE + 1)) + @Surcharge as DECIMAL (10,2))
from SHIPMENT_HEADER sh
inner join (
		select CARRIER, 
			SERVICE, 
			CASE 
				WHEN CARRIER = 'UPS' AND SERVICE = 'Next Early AM'   THEN dbo.BHS_fn_GetUPSFuelSurcharge('1')
				WHEN CARRIER = 'UPS' AND SERVICE = 'Next Day Air'    THEN dbo.BHS_fn_GetUPSFuelSurcharge('2')
				WHEN CARRIER = 'UPS' AND SERVICE = 'Next Day Saver'  THEN dbo.BHS_fn_GetUPSFuelSurcharge('3')
				WHEN CARRIER = 'UPS' AND SERVICE = '2nd Day Air AM'  THEN dbo.BHS_fn_GetUPSFuelSurcharge('4')                --update 02.17.2021 used this calculation to get fuel surcharge and percent of freight to bill
                WHEN CARRIER = 'UPS' AND SERVICE = '2 Day Air'		 THEN dbo.BHS_fn_GetUPSFuelSurcharge('5')                --initially was under the impression that fuel surcharge would stay one number, but these functions     
                WHEN CARRIER = 'UPS' AND SERVICE = '3 Day Select'    THEN dbo.BHS_fn_GetUPSFuelSurcharge('6')				 --allow us to get the current FS, stole this code from [BHS_ExitPoint_CalcAdditionalFreightCharge]
                WHEN CARRIER = 'UPS' AND SERVICE = 'Ground'		     THEN dbo.BHS_fn_GetUPSFuelSurcharge('7')                                          
                WHEN CARRIER = 'UPS' AND SERVICE = 'World Exp. Plus' THEN dbo.BHS_fn_GetUPSFuelSurcharge('100')
                WHEN CARRIER = 'UPS' AND SERVICE = 'World Express'	 THEN dbo.BHS_fn_GetUPSFuelSurcharge('102')
                WHEN CARRIER = 'UPS' AND SERVICE = 'World Expedited' THEN dbo.BHS_fn_GetUPSFuelSurcharge('103')
				WHEN RATING_ID like 'FedEx Ground%' THEN @FedExFuelSurcharge		--changed to be like to avoid ®
				WHEN RATING_ID like 'FedEx Express%' THEN @FedExFuelSurchargeExpress
                ELSE 0 END FUEL_SURCHARGE,
			USER_DEF7/100 PercentOfFreightToBill
		from CARRIER
	) C
		on sh.CARRIER = c.CARRIER
		and ISNULL(sh.CARRIER_SERVICE, '') = ISNULL(c.SERVICE, '')
where SHIPMENT_ID = @ShipmentID

select @INDY_BASE_FREIGHT_CHARGE = BASE_FREIGHT_CHARGE, 
		@INDY_TOTAL_FREIGHT_CHARGE = TOTAL_FREIGHT_CHARGE			
from SHIPMENT_HEADER										
where shipment_id = @ShipmentID --and warehouse = 'Toyota MDC-East'


--update sh
--set base_freight_charge = @CaliBaseFreightCharge,
--    TOTAL_FREIGHT_CHARGE = @CALI_TOTAL_FREIGHT_CHARGE                     
--from SHIPMENT_HEADER sh
--where shipment_id = @ShipmentID --and warehouse = 'Toyota MDC-East'

INSERT INTO [dbo].[BHS_WarehouseRating]
           ([SHIPMENT_ID]
		   ,[CALI_BASE_FREIGHT_CHARGE]
           ,[CALI_TOTAL_FREIGHT_CHARGE]
           ,[INDY_BASE_FREIGHT_CHARGE]
           ,[INDY_TOTAL_FREIGHT_CHARGE]
           ,[DATE_TIME_STAMP]
		   ,[SURCHARGE])
     select
           @ShipmentID
           , @CaliBaseFreightCharge
           ,@CALI_TOTAL_FREIGHT_CHARGE
           ,@INDY_BASE_FREIGHT_CHARGE
           ,@INDY_TOTAL_FREIGHT_CHARGE
           ,getdate()
		   ,@Surcharge
		from SHIPMENT_HEADER where SHIPMENT_ID = @ShipmentID --and warehouse = 'Toyota MDC-East'


