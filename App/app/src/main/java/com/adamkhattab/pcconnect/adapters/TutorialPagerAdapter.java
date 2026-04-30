package com.adamkhattab.pcconnect.adapters;

import androidx.fragment.app.Fragment;
import androidx.fragment.app.FragmentManager;
import androidx.fragment.app.FragmentPagerAdapter;

import com.adamkhattab.pcconnect.fragments.TutorialFragment1;
import com.adamkhattab.pcconnect.fragments.TutorialFragment2;
import com.adamkhattab.pcconnect.fragments.TutorialFragment3;

public class TutorialPagerAdapter extends FragmentPagerAdapter {

    public TutorialPagerAdapter(FragmentManager fm) {
        super(fm);
    }

    @Override
    public Fragment getItem(int position) {
        // Return the appropriate fragment based on the position
        switch (position) {
            case 0:
                return new TutorialFragment1();
            case 1:
                return new TutorialFragment2();
            case 2:
                return new TutorialFragment3();
            default:
                return null;
        }
    }

    @Override
    public int getCount() {
        // Return the number of fragments in the ViewPager
        return 3;
    }
}
