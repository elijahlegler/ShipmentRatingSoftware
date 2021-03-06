USE [ILS_Toyota]
GO
/****** Object:  StoredProcedure [dbo].[BHS_MDC_Move_GetContainersOnShipment]    Script Date: 8/13/2021 8:09:20 AM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO



ALTER PROCEDURE [dbo].[BHS_MDC_Move_GetContainersOnShipment](
	@ShipmentID nvarchar(25)
)
AS

select CASE 
		WHEN (CEILING((CASE WHEN round(length, 0) = 0 THEN 1 ELSE round(length, 0) END * 
			 CASE WHEN round(width, 0) = 0 THEN 1 ELSE round(width, 0) END *						--this behemoth calculates dimensional weight then compares it to weight
			 CASE WHEN round(height, 0) = 0 THEN 1 ELSE round(height, 0) END) / 139)) > WEIGHT		--to determine which to send to the function
		THEN CEILING((CASE WHEN round(length, 0) = 0 THEN 1 ELSE round(length, 0) END * 
			 CASE WHEN round(width, 0) = 0 THEN 1 ELSE round(width, 0) END *						
			 CASE WHEN round(height, 0) = 0 THEN 1 ELSE round(height, 0) END) / 139)
		ELSE CEILING(WEIGHT) END 'WEIGHT', 
		0.00 'VOLUME', --VOLUME, 
		count(*) 'COUNT',
		sh.TOTAL_WEIGHT 'TOTAL_WEIGHT',
		 CASE 
				WHEN CARRIER = 'Fedex' and CARRIER_SERVICE = 'GROUND'																THEN 'FedexGround' --Fedex ground will not allow weights over 150 pounds
				WHEN CARRIER_SERVICE = 'GROUND'	or CARRIER_SERVICE = '3 Day Select'	or CARRIER_SERVICE like 'Express Saver'			THEN 'Ground'
				WHEN CARRIER = 'Fedex' and (CARRIER_SERVICE like '%International%')													THEN 'International' --international orders get sent as a whole rather than container by container per Angie
				ELSE 'Air'
		END 'Service'
from shipping_container sc
join shipment_header_view sh on sh.internal_shipment_num = sc.internal_shipment_num
where sc.container_id is not null and sh.shipment_id = @ShipmentID
group by weight, volume, length, width, height, total_weight, carrier, carrier_service
