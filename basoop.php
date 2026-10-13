<?php defined('SYSPATH') OR die('No direct access allowed.');

class Model_Basoop extends Model
{
	//https://app.swaggerhub.com/apis/basip/panel-web-api/2.3.0#/admin/post_desktop_message
	
	//public $baseurl = 'http://10.200.20.2/api';
	
	/*
	Модель вызывных панелей bas-ip
	объект хранит данные в БД СКУД в таблицах:
	bas_param - IP и port

Вер 1.2.
13.02.2025
[*]
Добавлен анализ мажорного значения версии API. Исходя из этого формируется строка поиска UID по номеру карты

вер 1.1
	[+]
		17.11.2023 добавлено получение логина и пароля из базы данных.
		
		25.03.2024 
[+]
в процедуру fixCardIdxErr добавлен второй параметр: что именно писать в поле Result.
[-]
в процедуре fixCardIdxErr добавлена проверка кода операции. Если 1, то пишем данные в cardidx. В остальных случаях в cardidx ничего не пишем (т.к. данных о карте уже нет).
[*]
в процедуре fixCardIdxErr при вставке данных теперь используется id_cardinev. ранее использовалась пара id_card - id_dev, но это было опасно, т.к. код карты мог быть неправильным.

[ToDo]
Выявилась ошибка подклюения, т.к. задан неправильный IP адрес 0.0.0.0. Это надо корректно обрабатывать. Задача для basoop.
	
	*/
	public $baseurl = '';
	public $account_type = '-';
	public $server_type = 'bas';
	public $statusOnline = false;
	public $tokenTTT='';// токен авторизации
	public $id_dev='';
	public $id_ctrl='';
	public $id_dev_door0='';
	public $id_dev_door1='';
	public $name='';
	public $device_model='';
	public $framework_version='';
	public $device_name=''; //модель устройства
	public $firmware_version='';
	public $api_version='';
	public $user='admin';
	public $pass='171120';
	public $errmess;//текст сообщения об ошибке
	public $limit=50;//количество записей на одной странице
	
	/*
	Инициализация устройства происходит по id_dev точки прохода 
	из таблиц bas_param выбираются параметры
	name - название контроллера
	baseurl - IP адрес контроллера
	*/
	
	public function init_dev($id)
	{
		$this->id_dev=$id;
		$sql='select first 1 d.name, d.id_ctrl, d2.id_dev as id_dev_door0,d3.id_dev as id_dev_door1, bp.intvalue as IP,
                bp4.strvalue as LOGIN,
                bp5.strvalue as PASS
                 from device d
                join bas_param bp on d.id_dev=bp.id_dev
                  left join bas_param bp4 on bp4.id_dev=d.id_dev  and bp4.param=\'LOGIN\'
                left join bas_param bp5 on bp5.id_dev=d.id_dev  and bp5.param=\'PASS\'
                join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader=0
                left join device d3 on d3.id_ctrl=d.id_ctrl and d3.id_reader=1
				where bp.param=\'IP\'
            and bp.id_dev='.$id;

			try {

				$query = DB::query(Database::SELECT, $sql)
				->execute(Database::instance('fb'))
				->as_array();

				foreach($query as $key=>$value)
				{
					
					$this->id_ctrl=Arr::get($value, 'ID_CTRL', 0);
					$this->id_dev_door0=Arr::get($value, 'ID_DEV_DOOR0', 0);
					$this->id_dev_door1=Arr::get($value, 'ID_DEV_DOOR1', 0);
					$this->name=Arr::get($value, 'NAME', 0);
					$this->user=Arr::get($value, 'LOGIN', 0);
					$this->pass=Arr::get($value, 'PASS', 0);
					$this->baseurl=long2ip(Arr::get($value, 'IP', 0));
					
				}

		} catch (Exception $e) {
			Log::instance()->add(Log::DEBUG, '#31 '.$e->getMessage());

		}	
			
			 $this->init($this->baseurl);
		
			 return;
	}
	
	/*
	Иницилизация объекта bas-ip
	*/
	public function init ($ip)
	{
		
		$this->baseurl=$ip;
		$a=$this->makeRequest();
		
		if($a)
		{
		$this->statusOnline=True;
		$this->account_type=Arr::get($a, 'account_type');
		$this->tokenTTT=Arr::get($a, 'token');
		$about=$this->about();
		
		$this->device_model=Arr::get($about, 'device_model');
		$this->framework_version=Arr::get($about, 'framework_version');
		$this->device_name=Arr::get($about, 'device_name');
		$this->firmware_version=Arr::get($about, 'firmware_version');
		
		//$this->api_version=explode(".", Arr::get($about, 'api_version'));
		$this->api_version=Arr::get($about, 'api_version');
			
		} else {
			$this->statusOnline=False;
			$this->account_type='no';
			
		}
		return;
	
	}
	
	/*
	20.05.2023
	Сохранение конфигурационных параметров
	*/
	
	public function changeConfigIP ($data)// запись IP адреса в таб
	{
		//echo Debug::vars('38', $data); exit;
		$sql='delete from bas_param bp
				where bp.id_dev='.Arr::get($data, 'id_dev').'
				and bp.param=\'IP\'';
			try {

			$query2 = DB::query(Database::DELETE, $sql)
			->execute(Database::instance('fb'))
			;
			//Log::instance()->add(Log::DEBUG, '#47 Данные из набора '.Debug::vars($data).' удалеы успешно перед последующей вставкой. SQL='.$sql);
			
				$sql='INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE) VALUES ('.Arr::get($data, 'id_dev').', \'IP\',  \''.ip2long(Arr::get($data, 'new_IP')).'\');';
				try{
					$query2 = DB::query(Database::INSERT, $sql)
					->execute(Database::instance('fb'))
						;
					//Log::instance()->add(Log::DEBUG, '#54 Данные из набора '.Debug::vars($data).' вставлены успешно. SQL='.$sql);
				}  catch (Exception $e) {
					//Log::instance()->add(Log::DEBUG, '#56 вставка данных  из набора '.Debug::vars($data).' прошла с ошибкой. SQL='.$sql.' '. $e->getMessage());
				$query=0;
				//echo Debug::vars('47','exit'); exit;
				$result=1; // т.е. ошибка, надо разбираться.
			}
		
			} catch (Exception $e) {
				Log::instance()->add(Log::DEBUG, '#51 '.$e->getMessage());
				$query=0;
				//echo Debug::vars('47','exit'); exit;
				$result=1; // т.е. ошибка, надо разбираться.
			}
				
				
		
		return $result;
	}
	
	
	/*
	20.05.2023
	Кандидат на удаление
	*/

	
	
	public function getBasServerList()// получить id транспортных серверов типа bas
	{
		$sql='select d.* from server d
		    join servertypelist stl on stl.id_server=d.id_server
            join servertype stt on stt.id=stl.id_type
            where stt.sname=\''.$this->server_type.'\''; 
		try {

				$query = DB::query(Database::SELECT, $sql)
				->execute(Database::instance('fb'))
				->as_array()
				;
				return array ('status'=>0, 'res'=>$query);
		
		} catch (Exception $e) {
			Log::instance()->add(Log::DEBUG, '#28 '.$e->getMessage());
			
			return array ('status'=>1, 'res'=>$e->getMessage());
		}	
		
	}
	
	
	/*
		20.05.2023
		Фиксация результата в базе данных СКУД
	
	*/
	public function fixCardIdxOK($datacard, $result_write )
	{
		//echo Debug::vars('12', $result_write, $datacard); //exit;
		if(Arr::get(Arr::get($result_write,'res'),'uid', '-1')>0)
		{
		 $sql='UPDATE CARDIDX SET
					DEVIDX = '.Arr::get(Arr::get($result_write,'res'),'uid', '-1').',
					LOAD_TIME = \'now\',
					LOAD_RESULT = \'OK UID='.Arr::get(Arr::get($result_write,'res'),'uid', '-1').'\'
				WHERE (ID_CARD = \''.Arr::get($datacard, 'ID_CARD').'\') AND (ID_DEV = '.Arr::get($datacard, 'ID_DEV').')';
		} else {
		 $sql='UPDATE CARDIDX SET
					DEVIDX = '.Arr::get(Arr::get($result_write,'res'),'uid', '-1').',
					LOAD_TIME = \'now\',
					LOAD_RESULT = \'OK\',
					NOTE = \'Identifier with that type and number already exist\'
				WHERE (ID_CARD = \''.Arr::get($datacard, 'ID_CARD').'\') AND (ID_DEV = '.Arr::get($datacard, 'ID_DEV').')';
			
			
		}
		
		//Log::instance()->add(Log::DEBUG, '90 fixCardIdxOK sql'. Debug::vars(Arr::get(Arr::get($result_write,'res'),'uid', '-1') , $datacard, $result_write, $sql));
		try {

				$query = DB::query(Database::UPDATE, $sql)
				->execute(Database::instance('fb'))
				;
				//echo Debug::vars('25',$query); exit;
		
		} catch (Exception $e) {
			Log::instance()->add(Log::DEBUG, '#28 '.$e->getMessage());
			$query=0;
		}
		
		
		return;
		
	}
	
	/**
	*
	*/
	
	public function fixCardIdxErr($datacard, $result ) // запись сообщения об ошибке в таблицу cardidx
	{
		echo Debug::vars('198', $datacard, $result); exit; 
		
		if(Arr::get($datacard, 'OPERATION') == 1)// если карта стоит в очереди на запись, то есть что фиксировать.  а если карта стоит на удаление, то ее уже нет в cardidx, и записывать мы ничего не сможем
		{
			 $sql='UPDATE CARDIDX SET
						
						LOAD_TIME = \'now\',
						LOAD_RESULT = \''.$result.'\',
						NOTE = \''.$result.'\'
					WHERE ID_CARDINDEV = '.Arr::get($datacard, 'ID_CARDINDEV');
				
				
		//echo Debug::vars('266', $sql); exit; 
			
			Log::instance()->add(Log::DEBUG, '282 '. Debug::vars($datacard).' '. $sql);
			try {

					$query = DB::query(Database::UPDATE, iconv('UTF-8', 'CP1251', $sql))
					->execute(Database::instance('fb'))
					;
					//echo Debug::vars('25',$query); exit;
			
			} catch (Exception $e) {
				Log::instance()->add(Log::DEBUG, '#28 '.$e->getMessage());
				$query=0;
			}
		}
		
		return;
		
	}
	
	public function fixCardIdxErrNoConnect($messErr, $id_dev ) // запись сообщения об ошибке messErr в таблицу cardidx для контроллерв, принадлежащим контроллеру шв_вумпри отсутствии связи
	{
		//echo Debug::vars('198', $messErr, $id_dev); exit;
		//echo Debug::vars('240', $messErr); //exit;
		//при  неудачном удалении карты из устройства сообщение об ошибке писать некуда, т.к. карты уже нет в табилце cardidx, а в таблице cardindev нет полей для записи результата.
//поэтому при неуспешном удалении карты в поле attempts ставится цифра 97, что надо понимать как Удаление не удалось, нет связи с устройством.		

		 $sql='UPDATE CARDIDX SET
					
					LOAD_TIME = \'now\',
					LOAD_RESULT =\''.Text::limit_chars( 'ERR '.str_replace("'", " ",  $messErr), 100).'\',
					NOTE = \''.Text::limit_chars(str_replace("'", " ",  $messErr), 100).'\'
				WHERE  (ID_DEV in (select d2.id_dev from device d
					join device d2 on d2.id_ctrl=d.id_ctrl
					where d.id_dev='.$id_dev.')
					and (ID_DB=1))';
					
		$sql2='UPDATE CARDINDEV
		SET ATTEMPTS = 97
		WHERE (id_dev in(select d2.id_dev from device d
								join device d2 on d2.id_ctrl=d.id_ctrl
								where d.id_dev='.$id_dev.')
								and (ID_DB=1))';
					
		
	
		//Log::instance()->add(Log::DEBUG, '251 '. $sql.' '. $sql2);
		
		try {

				$query = DB::query(Database::UPDATE, $sql)
				->execute(Database::instance('fb'))
				; 
				$query = DB::query(Database::UPDATE, $sql2)
				->execute(Database::instance('fb'))
				; 
				//echo Debug::vars('25',$query); exit;
		
		} catch (Exception $e) {
			Log::instance()->add(Log::DEBUG, '#273 '.$e->getMessage());
			$query=0;
		}
		
		
		return 0;
		
	}
	
	/*
	20.05.2023 
	Функция удаления номера карты из очереди в таблице cardindev
	
	*/
	
	public function delFromCardindev($datacard)
	{
		//echo Debug::vars('108', $datacard); //exit;
			$sql='delete from cardindev cd
				where cd.id_cardindev='.Arr::get($datacard,'ID_CARDINDEV');
				//echo Debug::vars('42',$sql); exit;
				try {

				$query2 = DB::query(Database::DELETE, $sql)
				->execute(Database::instance('fb'))
				;
				
				$result=0; // т.е. все хорошо.
		
				} catch (Exception $e) {
					Log::instance()->add(Log::DEBUG, '#28 '.$e->getMessage());
					$query=0;
					//echo Debug::vars('47','exit'); exit;
					$result=1; // т.е. ошибка, надо разбираться.
				}
			
	}
	
	
	public function getCardList($id_dev)
	{
			
		$sql='select cd.id_cardindev, cd.id_card, cd.id_dev,cd.operation from cardindev cd
            join device d on d.id_dev=cd.id_dev
            join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader is null
            where d2.id_dev='.$id_dev;
			
			
		//Log::instance()->add(Log::DEBUG, '210 выборка номеров карт для обработки SQL='. $sql);
		try {

				$query = DB::query(Database::SELECT, $sql)
				->execute(Database::instance('fb'))
				->as_array();
		} catch (Exception $e) {
			Log::instance()->add(Log::DEBUG, '#359 '. $e->getMessage());
			$query=array();
		} 
		
		return $query;
	}
	
	
	
	
	public function makeRequest()// авторизация
	{
		
		$user=$this->user;
		$pass=$this->pass;
		
		$token=$this->getToken($pass);
		
		$request = Request::factory('http://'.$this->baseurl.'/api/v1/login')
			->query(array('username'=>$user, 'password'=>$token))
			->headers("Accept", "application/json")
			//-> method(Request::GET);
			->headers("Authorization", 'Bearer ')
			-> method('GET');
		try{
		$response=$request->execute();
		//Log::instance()->add(Log::DEBUG, '17 '.Debug::vars($request, json_decode ($response->body(), true)));		
			$this->statusOnline=true;
			return json_decode ($response->body(), true);
		} catch (Kohana_Request_Exception $e){
			Log::instance()->add(Log::DEBUG, '253 Проблема с авторизацией для '.$this->baseurl.' '.$e->getMessage());	

			return array('account_type'=>'no', 'desc'=>$e->getMessage());
			
		}
		
	}
	
	public function getToken($pass)
	{
		return md5($pass);
	}
	
	/*
	20.05.2023 
	Получение технической информации о панели bas-ip.
	
	*/
	
	public function about()
	{
	
		$request = Request::factory('http://'.$this->baseurl.'/api/info')
		//$request = Request::factory('http://10.200.20.2/api/info')
				->headers("Accept", "application/json")
				-> method(Request::GET);
				//->headers("Authorization", $token);
		try
		{	
			$response=$request->execute();
			return json_decode ($response->body(), true);
		} catch (Kohana_Request_Exception $e) {
			$this->statusOnline=False;
			return $e->getMessage();
		}
	}
	
	
	public function addCard($card)
	{		
		$validation=Validation::factory(array('key'=>$card))
					->rule('key', 'not_empty')
					->rule('key', 'alpha_numeric')
					;
		if($validation->check())
		{
			$data=json_encode(array(
				'identifier_owner'=>array(
						'name'=>$card,
						'type'=>'owner'),
				'identifier_type'=>'card',
				'identifier_number'=>ltrim($card, '0'),
				'lock'=>'all'));
				try{
					//Log::instance()->add(Log::DEBUG, '49 '. Debug::vars($data));
					$request = Request::factory('http://'.$this->baseurl.'/api/v1/access/identifier')
							->headers("Accept", "application/json")
							->headers("Content-Type", "application/json")
							->headers("Authorization", 'Bearer '.$this->tokenTTT)
							->method('POST')
							->body($data)
							->execute();
				
					//Log::instance()->add(Log::DEBUG, '94 card='.$card.' '. Debug::vars(json_decode ($request->body(), true)));
					//return json_decode ($request->body(), true);
					return array('status'=>$request->status(), 'res'=>json_decode ($request->body(), true));
						} catch (Kohana_Request_Exception $e) {
						$this->errmess=$e->getMessage();
						return array ('status'=>0, 'res'=>$e->getMessage());
				}
		} else {
			$this->errmess=$e->validation();
			return array ('status'=>2, 'res'=>$validation->errMessage());
		}
	}
	
	
	public function getInfoCard($card)// попытка получить uid по номеру карты
	{		
//https://developers.bas-ip.com/android-panels/1.8.0/returns-identifier-entity для версии 1.0.7
//тут прямо указано /access/identifiers/item/:identifierUid
//https://developers.bas-ip.com/android-panels/2.9.0/get-identifier-entity
//https://developers.bas-ip.com/android-panels/2.9.0/get-access-identifier-items тут прямо указано /access/identifier/items, т.к. точно как у меня.

	//извлекаю мажорное значение версии панели
	$major_version=Arr::get(explode(".", $this->api_version), 0);
	
		$uri='/api/v1/access/identifier/items?filter_field=identifier_number&filter_type=equal&filter_format=string&filter_value='.$card;
		if($major_version == 1) $uri='/api/v1/access/identifier/items?page_number=1&limit=20&filter=identifier_number eq \''.$card.'\'';
			
		

 		//$request = Request::factory('http://'.$this->baseurl.'/api/v1/access/identifier/items?filter_field=identifier_number&filter_type=equal&filter_format=string&filter_value='.$card) //
		$request = Request::factory('http://'.$this->baseurl.$uri) //
 		
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				//->body($data)
				->execute();
	
		//echo Debug::vars('42', json_decode ($request->body(), true)); exit;
		//Log::instance()->add(Log::DEBUG, '59 '. Debug::vars(json_decode ($request->body(), true)));
		//return Arr::get(Arr::get(Arr::get(json_decode ($request->body(), true), 'list_items'), 0), 'identifier_uid');
		return array('status'=>$request->status(), 'res'=>Arr::get(Arr::get(Arr::get(json_decode ($request->body(), true), 'list_items'), 0), 'identifier_uid'));
	}
	
	
	public function getInfoUID($uid)// попытка получить информацию по пользователе по uid
	{		
		//$request = Request::factory('http://10.200.20.2/api/v1/access/identifier/item/'.$uid) //
		$request = Request::factory('http://'.$this->baseurl.'/api/v1/access/identifier/item/'.$uid) //
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				//->body($data)
				->execute();
	
		
		return array('status'=>$request->status(), 'res'=>json_decode ($request->body(), true));
	}
	
	
	/*
	// DELETE /api/v1/access/identifier/item/2 HTTP/1.1
	//удаление происходит по номеру пользователя UID, а не по номеру карты.
	*/
	
	public function delUid($uid)
	{		
		$request = Request::factory('http://'.$this->baseurl.'/api/v1/access/identifier/item/'.$uid)
			->headers("Accept", "application/json")
			->method(Request::DELETE)
			->headers("Authorization", 'Bearer '.$this->tokenTTT);
			try{
			$response=$request->execute();
  
			//Log::instance()->add(Log::DEBUG, '136 карта iud='.$uid.' удалена успешно. '. Debug::vars($response->status()));
			//Log::instance()->add(Log::DEBUG, '372 iud='.$uid.' '. Debug::vars(json_decode ($request->body(), true))."\r\n\r\n");
			//echo Debug::vars('42',$response->status(), json_decode ($response->body(), true)); //exit;
			//Log::instance()->add(Log::DEBUG, '59 '.Debug::vars($response->status()).' '. Debug::vars(json_decode ($request->body(), true)));
			return array('status'=>$response->status(), 'res'=>json_decode ($response->body(), true));
		} catch (Kohana_Request_Exception $e) {
				return array ('status'=>0, 'res'=>$e->getMessage());
		}
	}
	
	
	/*
	// DELETE /api/v1/access/identifier/item/2 HTTP/1.1
	//удаление происходит по номеру пользователя UID, а не по номеру карты.
	*/
	
	 public function delCard($id_card)
	{		
		$data=$this->getInfoCard($id_card);//получил результат запроса UID по номеру карты
			
			switch (Arr::get($data, 'status'))
			{	
				case 200:
				//получен UID результат запроса
				if(Arr::get($data, 'res')) //если имеется UID
				{
				
				$data2=$this->delUid(Arr::get($data, 'res'));// удаляю карту из панели указав UID
						if(Arr::get($data2, 'status') == 200)
						{
						return array ('status'=>200, 'res'=>'OK');
						} else {
						// не смогу удалить карту из панели
						return array ('status'=>0, 'res'=>'564 Не удалось выяснить UID карты '.$id_card.'. Ответ '. Debug::vars(Arr::get($data2, 'res')));
						}
				}
				
				if( is_Null(Arr::get($data, 'res'))) //этой карты нет в устройстве, что и требуется от команды. Возвращаю ОК. 
				{
					return array ('status'=>200, 'res'=>'OK');
				}
				Log::instance()->add(Log::DEBUG, 'command deletekey device="'.$this->api_version.'", key="'.Arr::get($value, 'ID_CARD').'". Result: OK');
				break;

				default:
				
					return array ('status'=>0, 'res'=>'564 Не удалось выяснить UID карты '.$id_card.'. Ответ '. Debug::vars(Arr::get($data, 'res')));
	
				break;
			}
										
	}
	
	
	
	public function getLastEventID()// получить метку времени последнего события. Метка берется из базы данных СКУД
	{
		$sql='select bp.strvalue as LASTEVENT from bas_param bp
			where bp.id_dev='.$this->id_dev.' and bp.param=\'LASTEVENT\'';
		
		$query = DB::query(Database::SELECT, $sql)
				->execute(Database::instance('fb'))
				->get('LASTEVENT');
		//Log::instance()->add(Log::DEBUG, '#494 Получен lastvent '.$query.' SQL='.$sql);
		return $query;

	}		

	public function setLastEventID($eventid)// установить метку времени последнего события
	{
		//удаляю старый setLastEventID
		$sql='delete from bas_param bp
			where bp.id_dev='.$this->id_dev.' and bp.param=\'LASTEVENT\'';
		
			try {

			$query2 = DB::query(Database::DELETE, $sql)
			->execute(Database::instance('fb'))
			;
			
			//Log::instance()->add(Log::DEBUG, '#504 Старый as event удален успешно. SQL='.$sql);
				$sql='INSERT INTO BAS_PARAM (ID_DEV, PARAM, STRVALUE) VALUES ('.$this->id_dev.', \'LASTEVENT\',  \''.$eventid.'\');';
				try{
					$query2 = DB::query(Database::INSERT, $sql)
					->execute(Database::instance('fb'))
						;
						//Log::instance()->add(Log::DEBUG, '#504 Новый lastvent вставлен успешно. SQL='.$sql);
						$result=0; // вставка прошла успешно.	
					
				}  catch (Exception $e) {
					//Log::instance()->add(Log::DEBUG, '#513 вставка данных  lastvent прошла с ошибкой. SQL='.$sql.' '. $e->getMessage());
				$query=0;
				//echo Debug::vars('47','exit'); exit;
				$result=1; // т.е. ошибка, надо разбираться.
			}
		
			} catch (Exception $e) {
				Log::instance()->add(Log::DEBUG, '#51 Удаление старого lastevent произошло с ошибкой '.$e->getMessage());
				$query=0;
				//echo Debug::vars('47','exit'); exit;
				$result=1; // т.е. ошибка, надо разбираться.
			}
				
				
		
		return $result;
		//return 0;

	}		

	
	/* 
	$param - имя параметра
	$intvalue - параметр формата int
	$strvalue - параметр формата string
	*/
	public function saveParam($param, $intvalue, $strvalue)// запись (обновление) параметра в таблице bas_param 8.03.2023
	{
		//удаляю старое значение параметра
		$sql='delete from bas_param bp
			where bp.id_dev='.$this->id_dev.' and bp.param=\''.$param.'\'';
		
			try {

			$query2 = DB::query(Database::DELETE, $sql)
			->execute(Database::instance('fb'))
			;
			
			if(is_null($intvalue)) $sql='INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE, STRVALUE) VALUES ('.$this->id_dev.', \''.$param.'\', NULL,\''.$strvalue.'\')';
			if(is_null($strvalue)) $sql='INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE, STRVALUE) VALUES ('.$this->id_dev.', \''.$param.'\', '.$intvalue.', NULL)';
				//Log::instance()->add(Log::DEBUG, '#580. SQL='.$sql);
				try{
					$query2 = DB::query(Database::INSERT, $sql)
					->execute(Database::instance('fb'))
						;
					$result=0; // вставка прошла успешно.	
					
				}  catch (Exception $e) {
					
				$query=0;
				//echo Debug::vars('47','exit'); exit;
				$result=1; // т.е. ошибка, надо разбираться.
			}
		
			} catch (Exception $e) {
				Log::instance()->add(Log::DEBUG, '#51 Удаление старого lastevent произошло с ошибкой '.$e->getMessage());
				$query=0;
				//echo Debug::vars('47','exit'); exit;
				$result=1; // т.е. ошибка, надо разбираться.
			}
				
				
		
		return $result;
		//return 0;

	}		

	public function EventInsert($id_db=1, $id_eventtype = NULL, $id_cntrl= NULL, $id_reader= NULL, $note= NULL, $time= NULL, $id_video= NULL, $id_user= NULL, $ess1= NULL, $ess2= NULL, $idsource= NULL, $idserverts= NULL)// вставка события от устройства
	{
		
		//EXECUTE PROCEDURE DEVICEEVENTS_INSERT(NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
		//$sql="EXECUTE PROCEDURE DEVICEEVENTS_INSERT(1, ".Arr::get($eventcode, 'eventName').", ".$this->id_ctrl.", ".$this->id_reader.", '000001001A', 'now', NULL, 1, 2, 1, NULL, NULL)";// выполняется, событие вставляется, но не происходит автообновление Монитора. Надо обновлять вручную.
		$sql="EXECUTE PROCEDURE DEVICEEVENTS_INSERT(".$id_db.", ".$id_eventtype.", ".$id_cntrl.", ".$id_reader.", '".$note."', '". $time."', NULL, NULL, NULL, 1, ".$idsource.", ".(int)($idserverts / 1000).")";// выполняется, событие вставляется, но не происходит автообновление Монитора. Надо обновлять вручную.
			
		//Log::instance()->add(Log::DEBUG, '#562 sql=  '.$note.' , '.$sql);	//exit;
			try {

			DB::query(Database::SELECT, $sql)
			->execute(Database::instance('fb'))
			->as_array();
			
			//Log::instance()->add(Log::DEBUG, '#569 Событие вставлено успешно. SQL='.$sql);
			$result=1;
			} catch (Exception $e) {
				//Log::instance()->add(Log::DEBUG, '#572 Событие вставлено с ошибкой с ошибкой '.$e->getMessage());
				$query=0;
				
				$result=1; // т.е. ошибка, надо разбираться.
			}
				
		
		
		return $result;
	}		

	public function getEvent()// получить последние события
	{
		
		
		$from=$this->getLastEventID();// получить дату последного прочитанного события.
		//$from=1790812801000;// c 1 окт 2026 г
		//Log::instance()->add(Log::DEBUG, '#545 Получение событий начиная с метки '.date ('d.m.Y H:i:s',((int)($from/1000))).' ('.$from.' '.((int)($from/1000)).')');
		$totalEventCount=$this->getTotalCount($this->getLog($from, 50));//получил количество записей в журнале событий
		Log::instance()->add(Log::DEBUG, '751 Количество событий='.$totalEventCount. 'количество страниц='.ceil($totalEventCount/$this->limit)); 
		//Log::instance()->add(Log::DEBUG, '753 количество страниц='.ceil($totalEventCount/$this->limit));
		return $this->getLog($from, $this->limit, ceil($totalEventCount/$this->limit));

	}		
	
	private function getTotalCount($data)
	{
		
		$var1=Arr::get($data, 'res');
		$totalItems=$var1['list_option']['pagination']['total_items'];
		//Log::instance()->add(Log::DEBUG, '758 totalEventCount='.$totalItems); 
		return $totalItems;
		
	}

	public function getLog($from = 0, $limit=50, $page=1)// получить лог-файл
		{
			$toms = sprintf('%.0f', microtime(true) * 1000);
			//$toms = 1791676800000;
			//$toms = 1791656344550;
			//Log::instance()->add(Log::DEBUG, '#758 Получение событий начиная с метки '.date ('d.m.Y H:i:s',((int)($from/1000))).' ('.$from.') - '.date ('d.m.Y H:i:s',((int)($toms/1000))).' ('.$toms.')');
			
			
			
			$request = Request::factory('http://'.$this->baseurl.'/api/v1/log/items')
				->query(array(
					'locale'=>'ru',
					'from'=>$from,
					//'to'=>$toms,
					'limit'=>$limit,
					'page_number' => 2,
					'sort_type' => 'asc',
					// 'filter_field'=>'category', 
					// 'filter_type'=>'equal', 
					// 'filter_value'=>'access'
					))
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET');
			
			
			$response=$request->execute();
			
			 return array(
            'status' => $response->status(),
            'res'    => json_decode($response->body(), true),
        );
		}
		
	/**30.09.2025 очистить таблицы
	*Possible values: [apartments, identifiers, time_profiles, forwards, messages, logs]
	*
	*/
	public function clearDatabaseTables()// очистить таблицы
		{
			$data=json_encode(array(
				'clear_fields'=>array(
						'logs'),
				));
				try{
					//Log::instance()->add(Log::DEBUG, '49 '. Debug::vars($data));
					$request = Request::factory('http://'.$this->baseurl.'/api/v1/system/data/clear')
							->headers("Accept", "application/json")
							->headers("Content-Type", "application/json")
							->headers("Authorization", 'Bearer '.$this->tokenTTT)
							->method('POST')
							->body($data)
							->execute();
				
					//Log::instance()->add(Log::DEBUG, '94 card='.$card.' '. Debug::vars(json_decode ($request->body(), true)));
					//return json_decode ($request->body(), true);
					return array('status'=>$request->status(), 'res'=>json_decode ($request->body(), true));
				} catch (Kohana_Request_Exception $e) {
						$this->errmess=$e->getMessage();
						return array ('status'=>0, 'res'=>$e->getMessage());
				}
			
		}
		
	
	public function getTime()// прочитать время
		{
				$request = Request::factory('http://'.$this->baseurl.'/api/v1/device/time')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				->execute();
		//return json_decode ($request->body(), true);
		return json_decode ($request->body(), true);
		}
	
	
	public function setTime()// установить время
		{
		
		$ntp=$this->getNTPStatus();
		if(!Arr::get($ntp, 'enabled'))
		{
		$data='{
				  "timezone": "UTC+03:00",
				  "time_unix": '.time().'
				}';	
			
			$request = Request::factory('http://'.$this->baseurl.'/api/v1/network/timezone/manual')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('POST')
				->body($data)
				->execute();
				return json_decode ($request->body(), true);
		} else {
			Log::instance()->add(Log::DEBUG, '#677 Установка времени '.$this->baseurl.' невозможна. Для установки времени необходимо выключить NTP в настройках устройства.');
			return 1;
		}
		
		
		}
		
	public function getNTPStatus()// получить статус NTP
		{
		
		$data='{
				  "timezone": "UTC+03:00",
				  "time_unix": '.time().'
				}';	
			
			$request = Request::factory('http://'.$this->baseurl.'/api/v1/network/ntp')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				->body($data)
				->execute();
		
		return json_decode ($request->body(), true);
		}
		
	public function checkCardUid($card, $uid)// получить статус NTP
		{
			$res=false;
			$data = $this->getInfoUID($uid);
			//echo Debug::vars('839', $uid, Arr::get(Arr::get($data, 'res'), 'identifier_number')); 
			if($card == Arr::get(Arr::get($data, 'res'), 'identifier_number')) $res= true;
			return $res;
		
		}
		
	/* 13.03.2025 получить /network/management/server
	*/
	public function getManagementServer()
	{
			$request = Request::factory('http://'.$this->baseurl.'/api/v1/network/management/server')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				->execute();
		
		return json_decode ($request->body(), true);
		
	}
	
	
	/* 13.03.2025 установить /network/management/server
	*/
	public function setManagementServer($mode='http', $is_enabled=true, $heartbeat=true)
	{
		
		switch ($mode){
			case('mqtt'):			
				$data=json_encode(array(
						'is_enabled'=>$is_enabled,
						'protocol'=> 'mqtt',
						'settings'=>array(
							'url'=> '194.87.237.67:1883',
							'password'=> '123abc',
							'is_logging_enabled'=>true,
							'is_encrypted'=> false,
							'is_heartbeat_enabled'=> $heartbeat
							
						  ))); 
			break;
			case('http'):
			default:
				$data=json_encode(array(
						'is_enabled'=>$is_enabled,
						'protocol'=> 'http',
						'settings'=>array(
							'url'=> '192.168.10.6',
							'password'=> '123abc',
							'is_logging_enabled'=>true,
							'is_encrypted'=> true,
							'is_heartbeat_enabled'=> $heartbeat
						  ))); 
			break;	
		}
				$request = Request::factory('http://'.$this->baseurl.'/api/v1/network/management/server')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('POST')
				->body($data)
				->execute();
		return json_decode ($request->body(), true);
	}
	
	/* 13.03.2025 получить /network/management/server
	*/
	public function getManagementLink()
	{
			$request = Request::factory('http://'.$this->baseurl.'/api/v1/network/management/link')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				->execute();
		
		return json_decode ($request->body(), true);
		
	}
	
	
	/* 13.03.2025 установить /network/management/server
	*/
	public function setManagementLink($mode='http', $is_enabled=true, $heartbeat=true)
	{
		switch ($mode){
			case('mqtt'):			
				$data=json_encode(array(
						'link_enable'=>$is_enabled,
						'protocol'=> 'mqtt',
						'link_url'=> '194.87.237.67:1883',
						'link_password'=> '123abc',
						'realtime_logging '=>true,
						'heartbeat '=> $heartbeat
						)); 
			break;
			case('http'):
			default:
				$data=json_encode(array(
						'link_enable'=>$is_enabled,
						'link_url'=> '192.168.10.6',
						'link_password'=> '123abc',
						'realtime_logging'=>true,
						'heartbeat'=> $heartbeat
						));  
			break;	
		}
				$request = Request::factory('http://'.$this->baseurl.'/api/v1/network/management/link')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('POST')
				->body($data)
				->execute();
		return json_decode ($request->body(), true);
		
	}
	
	
	
	/* 13.03.2025 Get info about using certificate for remote server MQTT protocol
	* https://developers.bas-ip.com/android-panels/2.9.0/get-info-about-using-certificate-for-remote-server-mqtt-protocol
	*/
	public function getMqttAboutCertificate()
	{
					
				$request = Request::factory('http://'.$this->baseurl.'/api/v1/network/management/server/certificate')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				
				->execute();
		
		return json_decode ($request->body(), true);
		
	}
	
	
	
	/* 13.03.2025 Get info about using certificate for remote server MQTT protocol
	* https://developers.bas-ip.com/android-panels/2.9.0/get-info-about-using-certificate-for-remote-server-mqtt-protocol
	*/
	public function logRequest()
	{
					
				$data='log test1520';
				$request = Request::factory('http://'.$this->baseurl.'/api/v1/log')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('GET')
				
				->execute();
		
		return json_decode ($request->body(), true);
		
	}
	
	
	/* 13.03.2025 ,быстрый лог
	* 
	*/
	public function quitLog($mess)
	{
			$data=json_encode(array(
						'mess'=>'mess',
						
						  )); 		
				$request = Request::factory('http://localhost/alog/log')
				->headers("Accept", "application/json")
				->headers("Content-Type", "application/json")
				->headers("Authorization", 'Bearer '.$this->tokenTTT)
				->method('POST')
				->body($mess)
				->execute();
		
		return json_decode ($request->body(), true);
		
	}
	
	
	
	
	
}


