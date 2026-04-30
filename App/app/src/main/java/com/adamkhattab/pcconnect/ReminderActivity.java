package com.adamkhattab.pcconnect;
import android.app.DatePickerDialog;
import android.app.TimePickerDialog;
import android.content.Intent;
import android.os.AsyncTask;
import android.os.Bundle;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

import java.io.IOException;
import java.util.Calendar;
import java.util.Locale;

import okhttp3.FormBody;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

public class ReminderActivity extends AppCompatActivity {

    private Button selectDateButton;
    private Button selectTimeButton;
    private TextView timeTextView;
    private TextView dateTextView;
    private Button sendReminderButton;
    private EditText reminderEditText;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_reminder);

        selectDateButton = findViewById(R.id.select_date_button);
        selectTimeButton = findViewById(R.id.select_time_button);
        timeTextView = findViewById(R.id.TimeText);
        dateTextView = findViewById(R.id.DateText);
        reminderEditText = findViewById(R.id.reminder);
        String apiKey = SharedPrefManager.getInstance(this).getApiKey();

        selectDateButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showDatePicker();
            }
        });

        selectTimeButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showTimePicker();
            }
        });

        // Adding the sendReminderButton click listener
        Button sendReminderButton = findViewById(R.id.send_reminder_button);
        sendReminderButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                String selectedDate = dateTextView.getText().toString().replace("Selected Date: ", "");
                String selectedTime = timeTextView.getText().toString().replace("Selected Time: ", "");
                String reminderText = reminderEditText.getText().toString(); // Get text from EditText

                // Validate the fields
                if (selectedDate.isEmpty() || selectedTime.isEmpty() || reminderText.isEmpty()) {
                    Toast.makeText(ReminderActivity.this, "Please fill in all fields.", Toast.LENGTH_SHORT).show();
                    return; // Exit the method without sending the reminder
                }

                // Call the method to send the POST request
                sendReminder(apiKey, selectedDate, selectedTime, reminderText);
            }
        });

    }
    private void showDatePicker() {
        Calendar calendar = Calendar.getInstance();
        int year = calendar.get(Calendar.YEAR);
        int month = calendar.get(Calendar.MONTH);
        int day = calendar.get(Calendar.DAY_OF_MONTH);

        DatePickerDialog datePickerDialog = new DatePickerDialog(
                this,
                (view, selectedYear, selectedMonth, selectedDay) -> {
                    // Handle selected date
                    // You can update a TextView or store the selected date
                    String formattedMonth = String.format(Locale.getDefault(), "%02d", selectedMonth + 1);
                    String formattedDay = String.format(Locale.getDefault(), "%02d", selectedDay);
                    String selectedDate = formattedDay + "/" + formattedMonth + "/" + selectedYear;
                    dateTextView.setText("Selected Date: " + selectedDate);

                },
                year, month, day);

        datePickerDialog.show();
    }

    private void showTimePicker() {
        Calendar calendar = Calendar.getInstance();
        int hour = calendar.get(Calendar.HOUR_OF_DAY);
        int minute = calendar.get(Calendar.MINUTE);

        TimePickerDialog timePickerDialog = new TimePickerDialog(
                this,
                (view, selectedHour, selectedMinute) -> {
                    String formattedHour = String.format(Locale.getDefault(), "%02d", selectedHour);
                    String formattedMinute = String.format(Locale.getDefault(), "%02d", selectedMinute);
                    String selectedTime = formattedHour + ":" + formattedMinute;
                    timeTextView.setText("Selected Time: " + selectedTime);
                },
                hour, minute, true);

        timePickerDialog.show();
    }
    private void sendReminder(String apiKey, String selectedDate, String selectedTime, String reminderText) {
        new SendReminderTask().execute(apiKey, selectedDate, selectedTime, reminderText);
    }    private class SendReminderTask extends AsyncTask<String, Void, String> {
        @Override
        protected String doInBackground(String... params) {
            String apiKey = params[0];
            String selectedDate = params[1];
            String selectedTime = params[2];
            String reminderText = params[3];

            OkHttpClient client = new OkHttpClient();
            RequestBody requestBody = new FormBody.Builder()
                    .add("date", selectedDate)
                    .add("time", selectedTime)
                    .add("reminder", reminderText)
                    .build();

            Request request = new Request.Builder()
                    .url("https://pcconnect.adamkhattab.co.uk/api/pcconnect/reminder.php")
                    .addHeader("X-API-Key", apiKey)
                    .post(requestBody)
                    .build();

            try {
                Response response = client.newCall(request).execute();

                if (response.isSuccessful()) {
                    // Request successful
                    return response.body().string();
                } else {
                    // Request failed
                    return "Request failed: " + response.code();
                }
            } catch (IOException e) {
                e.printStackTrace();
                // Handle the exception
                return "Exception: " + e.getMessage();
            }
        }

        @Override
        protected void onPostExecute(String result) {
            // Handle the result, update UI, or show a message
            // For example, you could show a toast message with the result
            Toast.makeText(ReminderActivity.this, result, Toast.LENGTH_SHORT).show();
            Intent intent = new Intent(ReminderActivity.this, ReminderSuccess.class);
            startActivity(intent);
        }

    }

}
