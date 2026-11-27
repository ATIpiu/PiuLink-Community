package com.limelight.grid;

import android.content.Context;
import android.view.View;
import android.widget.ImageView;
import android.widget.ProgressBar;
import android.widget.TextView;

import com.limelight.PcView;
import com.limelight.R;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.preferences.PreferenceConfiguration;

import java.util.Collections;
import java.util.Comparator;

public class PcGridAdapter extends GenericGridAdapter<PcView.ComputerObject> {

    private final java.util.function.Consumer<ComputerDetails> openDesktop;
    private final java.util.function.Consumer<ComputerDetails> openLibrary;

    private final java.util.function.Consumer<ComputerDetails> openSuperScreen;
    private final java.util.function.Consumer<ComputerDetails> openInvite;

    public PcGridAdapter(Context context, PreferenceConfiguration prefs, java.util.function.Consumer<ComputerDetails> openDesktop) {
        this(context, prefs, openDesktop, null);
    }

    public PcGridAdapter(Context context, PreferenceConfiguration prefs,
                         java.util.function.Consumer<ComputerDetails> openDesktop,
                         java.util.function.Consumer<ComputerDetails> openLibrary) {
        this(context, prefs, openDesktop, openLibrary, null);
    }

    public PcGridAdapter(Context context, PreferenceConfiguration prefs,
                         java.util.function.Consumer<ComputerDetails> openDesktop,
                         java.util.function.Consumer<ComputerDetails> openLibrary,
                         java.util.function.Consumer<ComputerDetails> openSuperScreen) {
        this(context, prefs, openDesktop, openLibrary, openSuperScreen, null);
    }

    public PcGridAdapter(Context context, PreferenceConfiguration prefs,
                         java.util.function.Consumer<ComputerDetails> openDesktop,
                         java.util.function.Consumer<ComputerDetails> openLibrary,
                         java.util.function.Consumer<ComputerDetails> openSuperScreen,
                         java.util.function.Consumer<ComputerDetails> openInvite) {
        super(context, openDesktop != null ? R.layout.pc_touch_item : R.layout.pc_grid_item);
        this.openDesktop = openDesktop;
        this.openLibrary = openLibrary;
        this.openSuperScreen = openSuperScreen;
        this.openInvite = openInvite;
    }

    private int getColorCompat(int resource) {
        return android.os.Build.VERSION.SDK_INT >= 23
                ? context.getResources().getColor(resource, context.getTheme())
                : context.getResources().getColor(resource);
    }

    public void updateLayoutWithPreferences(Context context, PreferenceConfiguration prefs) {
        // This will trigger the view to reload with the new layout
        setLayoutId(openDesktop != null ? R.layout.pc_touch_item : R.layout.pc_grid_item);
    }

    public void addComputer(PcView.ComputerObject computer) {
        itemList.add(computer);
        sortList();
    }

    private void sortList() {
        Collections.sort(itemList, new Comparator<PcView.ComputerObject>() {
            @Override
            public int compare(PcView.ComputerObject lhs, PcView.ComputerObject rhs) {
                return lhs.details.name.toLowerCase().compareTo(rhs.details.name.toLowerCase());
            }
        });
    }

    public boolean removeComputer(PcView.ComputerObject computer) {
        return itemList.remove(computer);
    }

    @Override
    public void populateView(View parentView, ImageView imgView, ProgressBar prgView, TextView txtView, ImageView overlayView, PcView.ComputerObject obj) {
        TextView statusView = parentView.findViewById(R.id.grid_status);
        imgView.setImageResource(R.drawable.ic_computer);
        if (obj.details.state == ComputerDetails.State.ONLINE) {
            imgView.setAlpha(1.0f);
        }
        else {
            imgView.setAlpha(0.4f);
        }

        if (obj.details.state == ComputerDetails.State.UNKNOWN) {
            prgView.setVisibility(View.VISIBLE);
        }
        else {
            prgView.setVisibility(View.INVISIBLE);
        }

        txtView.setText(obj.details.name);
        txtView.setAlpha(1.0f);

        String statusText;
        int statusColor;
        switch (obj.details.state) {
            case ONLINE:
                if (obj.details.activeAddress != null && com.limelight.computers.DirectInviteTunnel.isEndpoint(obj.details.activeAddress.address)) {
                    statusText = context.getString(R.string.pcview_status_online_p2p);
                } else if (obj.details.activeAddress != null &&
                        obj.details.activeAddress.equals(obj.details.localAddress)) {
                    statusText = context.getString(R.string.pcview_status_online_lan);
                } else if (obj.details.activeAddress != null &&
                        obj.details.activeAddress.equals(obj.details.manualAddress) &&
                        com.limelight.utils.NetHelper.isPrivateHostAddress(obj.details.activeAddress.address)) {
                    statusText = context.getString(R.string.pcview_status_online_private);
                } else {
                    statusText = context.getString(R.string.pcview_menu_header_online);
                }
                statusColor = R.color.piulink_success;
                break;
            case OFFLINE:
                statusText = context.getString(R.string.pcview_menu_header_offline);
                statusColor = R.color.piulink_danger;
                break;
            default:
                statusText = context.getString(R.string.pcview_menu_header_unknown);
                statusColor = R.color.piulink_warning;
                break;
        }
        if (openDesktop != null && obj.details.state == ComputerDetails.State.ONLINE &&
                obj.details.pairState == PairingManager.PairState.NOT_PAIRED) {
            statusText = context.getString(R.string.home_tap_to_pair, statusText);
            statusColor = R.color.home_muted;
        }
        statusView.setText(statusText);
        parentView.setContentDescription(obj.details.name + ", " + statusText);
        View desktop = parentView.findViewById(R.id.pcOpenDesktop);
        boolean available = openDesktop != null && obj.details.state == ComputerDetails.State.ONLINE &&
                obj.details.pairState == PairingManager.PairState.PAIRED && obj.details.serverCert != null;
        boolean canTryDesktop = available || (openDesktop != null && obj.details.serverCert != null &&
                obj.details.state != ComputerDetails.State.ONLINE);
        statusView.setTextColor(getColorCompat(available ? R.color.home_device_online : statusColor));
        if (openDesktop != null) {
            View invite = parentView.findViewById(R.id.pcSavedInvite);
            boolean hasInvite = openInvite != null && obj.details.serverCert != null &&
                    com.limelight.computers.ConnectionHistory.hasAcceptedInvite(context, obj.details.uuid);
            invite.setVisibility(hasInvite ? View.VISIBLE : View.GONE);
            invite.setContentDescription(hasInvite ? context.getString(R.string.open_invite_named, obj.details.name) : null);
            invite.setOnClickListener(hasInvite ? v -> openInvite.accept(obj.details) : null);
            // A single local illustration keeps additional computers in a compact list.
            parentView.findViewById(R.id.pcLunarCover).setVisibility(
                    !itemList.isEmpty() && itemList.get(0) == obj ? View.VISIBLE : View.GONE);
            parentView.findViewById(R.id.grid_image_layout).setVisibility(available ? View.GONE : View.VISIBLE);
            parentView.setActivated(available);
            txtView.setTextColor(getColorCompat(available ? android.R.color.white : R.color.home_ink));
            imgView.setColorFilter(getColorCompat(available ? R.color.home_device_action : R.color.home_muted));
            statusView.setCompoundDrawablesRelativeWithIntrinsicBounds(available ? R.drawable.pc_online_dot : 0, 0, 0, 0);
            parentView.findViewById(R.id.pcActions).setVisibility(canTryDesktop ? View.VISIBLE : View.GONE);
            View superScreen = parentView.findViewById(R.id.pcSuperScreen);
            boolean superAvailable = available && openSuperScreen != null;
            superScreen.setVisibility(superAvailable ? View.VISIBLE : View.GONE);
            superScreen.setContentDescription(superAvailable ? context.getString(R.string.pc_super_screen_named, obj.details.name) : null);
            superScreen.setOnClickListener(superAvailable ? v -> openSuperScreen.accept(obj.details) : null);
            View library = parentView.findViewById(R.id.pcOpenLibrary);
            boolean libraryAvailable = available && openLibrary != null;
            library.setVisibility(libraryAvailable ? View.VISIBLE : View.GONE);
            library.setContentDescription(libraryAvailable ? context.getString(R.string.home_apps_named, obj.details.name) : null);
            library.setOnClickListener(libraryAvailable ? v -> openLibrary.accept(obj.details) : null);
        }
        desktop.setVisibility(canTryDesktop ? View.VISIBLE : View.GONE);
        desktop.setContentDescription(canTryDesktop ? context.getString(R.string.pc_open_desktop_named, obj.details.name) : null);
        desktop.setOnClickListener(canTryDesktop ? v -> openDesktop.accept(obj.details) : null);

        if (obj.details.state == ComputerDetails.State.OFFLINE) {
            overlayView.setImageResource(R.drawable.ic_pc_offline);
            overlayView.setColorFilter(getColorCompat(R.color.piulink_danger));
            overlayView.setAlpha(1.0f);
            overlayView.setVisibility(View.VISIBLE);
        }
        // We must check if the status is exactly online and unpaired
        // to avoid colliding with the loading spinner when status is unknown
        else if (obj.details.state == ComputerDetails.State.ONLINE &&
                obj.details.pairState == PairingManager.PairState.NOT_PAIRED) {
            overlayView.setImageResource(R.drawable.ic_lock);
            overlayView.setColorFilter(getColorCompat(R.color.piulink_blue));
            overlayView.setAlpha(1.0f);
            overlayView.setVisibility(View.VISIBLE);
        }
        else {
            overlayView.setVisibility(View.GONE);
        }
    }
}
