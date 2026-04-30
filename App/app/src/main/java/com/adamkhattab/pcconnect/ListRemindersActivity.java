package com.adamkhattab.pcconnect;
import android.os.AsyncTask;
import android.os.Bundle;
import android.widget.ArrayAdapter;
import android.widget.ListView;

import androidx.appcompat.app.AppCompatActivity;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;

public class ListRemindersActivity extends AppCompatActivity {

    String API_KEY = SharedPrefManager.getInstance(this).getApiKey();
    private static final String API_URL = "https://pcconnect.adamkhattab.co.uk/api/pcconnect/listreminders.php";

    private ListView listView;
    private ReminderAdapter adapter;  // Use the custom adapter

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_list_reminders);

        listView = findViewById(R.id.listView);
        adapter = new ReminderAdapter(this, new ArrayList<>());  // Initialize the custom adapter
        listView.setAdapter(adapter);

        new FetchRemindersTask().execute();
    }



    private class FetchRemindersTask extends AsyncTask<Void, Void, List<String>> {

        @Override
        protected List<String> doInBackground(Void... voids) {
            List<String> remindersList = new ArrayList<>();

            try {
                URL url = new URL(API_URL);
                HttpURLConnection connection = (HttpURLConnection) url.openConnection();
                connection.setRequestMethod("GET");
                connection.setRequestProperty("X-API-Key", API_KEY);

                int responseCode = connection.getResponseCode();
                if (responseCode == HttpURLConnection.HTTP_OK) {
                    BufferedReader reader = new BufferedReader(new InputStreamReader(connection.getInputStream()));
                    StringBuilder response = new StringBuilder();
                    String line;
                    while ((line = reader.readLine()) != null) {
                        response.append(line);
                    }
                    reader.close();

                    JSONArray jsonArray = new JSONArray(response.toString());
                    for (int i = 0; i < jsonArray.length(); i++) {
                        JSONObject jsonObject = jsonArray.getJSONObject(i);
                        String date = jsonObject.getString("Date");
                        String time = jsonObject.getString("Time");
                        String reminder = jsonObject.getString("Reminder");
                        int completed = jsonObject.getInt("Completed");

                        String status = (completed == 1) ? "Completed" : "Not Completed";
                        String reminderText = "Date: " + date + "\nTime: " + time + "\nReminder: " + reminder + "\nStatus: " + status;
                        remindersList.add(reminderText);
                    }
                }

            } catch (IOException | JSONException e) {
                e.printStackTrace();
            }

            return remindersList;
        }

        @Override
        protected void onPostExecute(List<String> reminders) {
            super.onPostExecute(reminders);
            adapter.addAll(reminders);
        }
    }

}
