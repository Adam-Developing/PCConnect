export namespace app {
	
	export class ConnectivityState {
	    socketHealthy: boolean;
	    mode: string;
	
	    static createFrom(source: any = {}) {
	        return new ConnectivityState(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.socketHealthy = source["socketHealthy"];
	        this.mode = source["mode"];
	    }
	}
	export class Session {
	    baseUrl: string;
	    apiKey: string;
	    pcName: string;
	    notificationStyle: string;
	    fullscreenBgColor: string;
	    fullscreenTextColor: string;
	
	    static createFrom(source: any = {}) {
	        return new Session(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.baseUrl = source["baseUrl"];
	        this.apiKey = source["apiKey"];
	        this.pcName = source["pcName"];
	        this.notificationStyle = source["notificationStyle"];
	        this.fullscreenBgColor = source["fullscreenBgColor"];
	        this.fullscreenTextColor = source["fullscreenTextColor"];
	    }
	}

}

export namespace reminders {
	
	export class Reminder {
	    ID: number;
	    Username: number;
	    Date: string;
	    Time: string;
	    Reminder: string;
	    Completed: number;
	
	    static createFrom(source: any = {}) {
	        return new Reminder(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.ID = source["ID"];
	        this.Username = source["Username"];
	        this.Date = source["Date"];
	        this.Time = source["Time"];
	        this.Reminder = source["Reminder"];
	        this.Completed = source["Completed"];
	    }
	}

}

