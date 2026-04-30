package com.adamkhattab.pcconnect;
import android.content.Context;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.TextView;

import androidx.annotation.NonNull;

import java.util.List;

public class ReminderAdapter extends ArrayAdapter<String> {

    public ReminderAdapter(Context context, List<String> reminders) {
        super(context, 0, reminders);
    }

    @NonNull
    @Override
    public View getView(int position, View convertView, @NonNull ViewGroup parent) {
        if (convertView == null) {
            convertView = LayoutInflater.from(getContext()).inflate(R.layout.list_item_layout, parent, false);
        }

        TextView textView = convertView.findViewById(R.id.itemTextView);
        String reminderText = getItem(position);
        textView.setText(reminderText);

        return convertView;
    }
}
